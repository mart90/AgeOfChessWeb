import pygame
import sys
import os
import json
from generate_board import fetch_board
from board import M, P, Board
from square import Square
from constants import PIECE_COSTS

ASSETS_DIR = os.path.join(os.path.dirname(__file__), "..", "client", "public", "assets")

# Colors (for overlays and panel only)
BG_COLOR = (30, 30, 30)
HIGHLIGHT_COLOR = (255, 255, 100, 120)
SELECTED_COLOR = (100, 180, 255, 140)
MINE_WHITE_BORDER = (80, 120, 220)
MINE_BLACK_BORDER = (220, 80, 80)
PANEL_BG = (40, 40, 45)
TEXT_COLOR = (220, 220, 220)
GOLD_COLOR = (255, 215, 0)
SHOP_AFFORDABLE = (60, 70, 80)
SHOP_UNAFFORDABLE = (45, 45, 50)
TURN_ACTIVE = (100, 200, 100)

SQUARE_SIZE = 80
PANEL_WIDTH = 220

# Map terrain_type + checkerboard parity to sprite filenames
SQUARE_SPRITES = {
    ("m", True): "dirt_mine.png",
    ("m", False): "grass_mine.png",
    ("r", True): "dirt_rocks.png",
    ("r", False): "grass_rocks.png",
    ("t", True): "dirt_trees.png",
    ("t", False): "grass_trees.png",
    (None, True): "dirt.png",
    (None, False): "grass.png",
}

PIECE_SPRITES = {
    ("k", True): "w_king.png",
    ("k", False): "b_king.png",
    ("q", True): "w_queen.png",
    ("q", False): "b_queen.png",
    ("r", True): "w_rook.png",
    ("r", False): "b_rook.png",
    ("b", True): "w_bishop.png",
    ("b", False): "b_bishop.png",
    ("n", True): "w_knight.png",
    ("n", False): "b_knight.png",
    ("p", True): "w_pawn.png",
    ("p", False): "b_pawn.png",
}

PIECE_NAMES = {"k": "King", "q": "Queen", "r": "Rook", "b": "Bishop", "n": "Knight", "p": "Pawn"}


def load_replay(filepath):
    """Load a replay JSON file and return (board, moves, result)."""
    with open(filepath) as f:
        data = json.load(f)

    bd = data["initialBoard"]
    squares = []
    for s in bd["squares"]:
        sq = Square(s["id"], s["x"], s["y"])
        sq.terrain_type = s.get("terrain")
        if "piece" in s:
            sq.piece_type = s["piece"]
            sq.piece_is_white = s["isWhite"]
        sq.has_treasure = s.get("treasure", False)
        if "ownedBy" in s:
            sq.owned_by = s["ownedBy"]
        squares.append(sq)

    board = Board(squares)
    board.white_gold = bd["whiteGold"]
    board.black_gold = bd["blackGold"]

    moves = []
    for m in data["moves"]:
        if m["type"] == "move":
            moves.append((M, m["from"], m["to"]))
        else:
            moves.append((P, m["piece"], m["dest"], m["isWhite"]))

    return board, moves, data["result"]


def load_sprite(subdir, filename, size):
    path = os.path.join(ASSETS_DIR, subdir, filename)
    img = pygame.image.load(path).convert_alpha()
    return pygame.transform.smoothscale(img, (size, size))


def run_ui():
    board = fetch_board()
    board_px = board.size * SQUARE_SIZE
    width = board_px + PANEL_WIDTH
    height = board_px

    pygame.init()
    screen = pygame.display.set_mode((width, height))
    pygame.display.set_caption("Age of Chess — Board Viewer")
    info_font = pygame.font.SysFont("Segoe UI", 18)
    shop_font = pygame.font.SysFont("Segoe UI", 16)
    gold_font = pygame.font.SysFont("Segoe UI", 22, bold=True)

    # Preload sprites
    square_imgs = {}
    for key, fname in SQUARE_SPRITES.items():
        square_imgs[key] = load_sprite("squares", fname, SQUARE_SIZE)

    piece_imgs = {}
    for key, fname in PIECE_SPRITES.items():
        piece_imgs[key] = load_sprite("objects", fname, SQUARE_SIZE)

    treasure_img = load_sprite("objects", "treasure.png", SQUARE_SIZE * 2 // 3)

    # Smaller piece sprites for shop
    shop_piece_imgs = {}
    for pt in ("q", "r", "b", "n", "p"):
        shop_piece_imgs[pt] = load_sprite("objects", PIECE_SPRITES[(pt, True)], 30)

    selected_square = None
    legal_dest_ids = []
    selected_shop_piece = None
    legal_placement_ids = []

    def get_legal_moves_for(source_id):
        moves = board.get_legal_moves()
        return [m for m in moves if m[0] == M and m[1] == source_id]

    def get_placement_moves_for(piece_type):
        moves = board.get_legal_moves()
        return [m for m in moves if m[0] == P and m[1] == piece_type]

    def draw():
        screen.fill(BG_COLOR)

        # Draw board
        for sq in board.squares:
            x = sq.x * SQUARE_SIZE
            y = (board.size - 1 - sq.y) * SQUARE_SIZE
            is_dirt = (sq.x + sq.y) % 2 == 0
            sprite_key = (sq.terrain_type, is_dirt)
            screen.blit(square_imgs[sprite_key], (x, y))

            # Mine ownership border
            if sq.terrain_type == "m" and sq.owned_by is not None:
                border_color = MINE_WHITE_BORDER if sq.owned_by == 0 else MINE_BLACK_BORDER
                pygame.draw.rect(screen, border_color, (x, y, SQUARE_SIZE, SQUARE_SIZE), 3)

            # Treasure
            if sq.has_treasure:
                tw = treasure_img.get_width()
                th = treasure_img.get_height()
                screen.blit(treasure_img, (x + (SQUARE_SIZE - tw) // 2, y + (SQUARE_SIZE - th) // 2))

            # Piece
            if sq.piece_type is not None:
                pkey = (sq.piece_type, sq.piece_is_white)
                screen.blit(piece_imgs[pkey], (x, y))

        # Highlight selected square
        if selected_square is not None:
            x = selected_square.x * SQUARE_SIZE
            y = (board.size - 1 - selected_square.y) * SQUARE_SIZE
            overlay = pygame.Surface((SQUARE_SIZE, SQUARE_SIZE), pygame.SRCALPHA)
            overlay.fill(SELECTED_COLOR)
            screen.blit(overlay, (x, y))

        # Highlight legal destinations
        for dest_id in legal_dest_ids:
            sq = board.squares[dest_id]
            x = sq.x * SQUARE_SIZE
            y = (board.size - 1 - sq.y) * SQUARE_SIZE
            overlay = pygame.Surface((SQUARE_SIZE, SQUARE_SIZE), pygame.SRCALPHA)
            overlay.fill(HIGHLIGHT_COLOR)
            screen.blit(overlay, (x, y))

        # Highlight legal placements
        for dest_id in legal_placement_ids:
            sq = board.squares[dest_id]
            x = sq.x * SQUARE_SIZE
            y = (board.size - 1 - sq.y) * SQUARE_SIZE
            overlay = pygame.Surface((SQUARE_SIZE, SQUARE_SIZE), pygame.SRCALPHA)
            overlay.fill(HIGHLIGHT_COLOR)
            screen.blit(overlay, (x, y))

        # Info panel
        px = board_px + 10
        pygame.draw.rect(screen, PANEL_BG, (board_px, 0, PANEL_WIDTH, height))

        # Turn indicator
        turn_text = "White's Turn" if board.white_is_active else "Black's Turn"
        txt = info_font.render(turn_text, True, TURN_ACTIVE)
        screen.blit(txt, (px, 15))

        # Gold
        wg = gold_font.render(f"White Gold: {board.white_gold}", True, GOLD_COLOR)
        screen.blit(wg, (px, 50))
        bg = gold_font.render(f"Black Gold: {board.black_gold}", True, GOLD_COLOR)
        screen.blit(bg, (px, 80))

        # Mines
        wm = info_font.render(f"White Mines: {board.mines_owned_by_white}", True, TEXT_COLOR)
        screen.blit(wm, (px, 115))
        bm = info_font.render(f"Black Mines: {board.mines_owned_by_black}", True, TEXT_COLOR)
        screen.blit(bm, (px, 140))

        # Shop
        shop_label = info_font.render("— Shop —", True, TEXT_COLOR)
        screen.blit(shop_label, (px + 50, 180))

        gold = board.active_player_gold()
        shop_rects.clear()
        for i, pc in enumerate(PIECE_COSTS):
            ry = 210 + i * 45
            affordable = pc["cost"] <= gold
            bg_col = SHOP_AFFORDABLE if affordable else SHOP_UNAFFORDABLE
            if selected_shop_piece == pc["type"]:
                bg_col = (100, 140, 180)
            r = pygame.Rect(px, ry, PANEL_WIDTH - 20, 38)
            pygame.draw.rect(screen, bg_col, r, border_radius=4)

            # Piece icon in shop
            screen.blit(shop_piece_imgs[pc["type"]], (px + 5, ry + 4))

            label = f"{PIECE_NAMES[pc['type']]}  —  {pc['cost']}g"
            color = TEXT_COLOR if affordable else (90, 90, 90)
            txt = shop_font.render(label, True, color)
            screen.blit(txt, (px + 40, ry + 10))
            shop_rects.append((r, pc["type"], affordable))

        pygame.display.flip()

    shop_rects = []

    running = True
    draw()
    while running:
        for event in pygame.event.get():
            if event.type == pygame.QUIT:
                running = False
                break

            if event.type == pygame.MOUSEBUTTONDOWN and event.button == 1:
                mx, my = event.pos

                # Check shop click
                if mx >= board_px:
                    clicked_shop = False
                    for rect, piece_type, affordable in shop_rects:
                        if rect.collidepoint(mx, my) and affordable:
                            if selected_shop_piece == piece_type:
                                selected_shop_piece = None
                                legal_placement_ids = []
                            else:
                                selected_shop_piece = piece_type
                                selected_square = None
                                legal_dest_ids = []
                                placement_moves = get_placement_moves_for(piece_type)
                                legal_placement_ids = [m[2] for m in placement_moves]
                            clicked_shop = True
                            break
                    if not clicked_shop:
                        selected_shop_piece = None
                        legal_placement_ids = []
                    draw()
                    continue

                # Board click
                bx = mx // SQUARE_SIZE
                by = board.size - 1 - (my // SQUARE_SIZE)
                if bx < 0 or bx >= board.size or by < 0 or by >= board.size:
                    continue

                clicked_id = by * board.size + bx
                clicked_sq = board.squares[clicked_id]

                # If we have a shop piece selected and click a valid placement
                if selected_shop_piece and clicked_id in legal_placement_ids:
                    move = (P, selected_shop_piece, clicked_id, board.white_is_active)
                    board.do_move(move)
                    selected_shop_piece = None
                    legal_placement_ids = []
                    selected_square = None
                    legal_dest_ids = []
                    draw()
                    continue

                # If clicking a highlighted destination, execute move
                if clicked_id in legal_dest_ids and selected_square is not None:
                    move = (M, selected_square.id, clicked_id)
                    board.do_move(move)
                    selected_square = None
                    legal_dest_ids = []
                    selected_shop_piece = None
                    legal_placement_ids = []
                    draw()
                    continue

                # If clicking own piece, select it
                if clicked_sq.piece_type is not None and clicked_sq.piece_is_white == board.white_is_active:
                    selected_square = clicked_sq
                    moves = get_legal_moves_for(clicked_id)
                    legal_dest_ids = [m[2] for m in moves]
                    selected_shop_piece = None
                    legal_placement_ids = []
                    draw()
                    continue

                # Deselect
                selected_square = None
                legal_dest_ids = []
                selected_shop_piece = None
                legal_placement_ids = []
                draw()

    pygame.quit()
    sys.exit()


def run_replay(filepath):
    """Replay a saved game with arrow keys to step through moves."""
    initial_board, moves, result = load_replay(filepath)
    board = initial_board.clone()
    board_px = board.size * SQUARE_SIZE
    width = board_px + PANEL_WIDTH
    height = board_px

    pygame.init()
    result_str = {1: "White wins", -1: "Black wins", 0: "Draw"}[result]
    screen = pygame.display.set_mode((width, height))
    pygame.display.set_caption(f"Replay — {os.path.basename(filepath)} — {result_str}")
    info_font = pygame.font.SysFont("Segoe UI", 18)
    gold_font = pygame.font.SysFont("Segoe UI", 22, bold=True)
    help_font = pygame.font.SysFont("Segoe UI", 14)

    square_imgs = {}
    for key, fname in SQUARE_SPRITES.items():
        square_imgs[key] = load_sprite("squares", fname, SQUARE_SIZE)
    piece_imgs = {}
    for key, fname in PIECE_SPRITES.items():
        piece_imgs[key] = load_sprite("objects", fname, SQUARE_SIZE)
    treasure_img = load_sprite("objects", "treasure.png", SQUARE_SIZE * 2 // 3)

    move_index = 0  # how many moves have been applied

    def rebuild_board(target_index):
        """Rebuild board state up to target_index moves."""
        b = initial_board.clone()
        for i in range(target_index):
            b.do_move(moves[i])
        return b

    def draw():
        screen.fill(BG_COLOR)

        for sq in board.squares:
            x = sq.x * SQUARE_SIZE
            y = (board.size - 1 - sq.y) * SQUARE_SIZE
            is_dirt = (sq.x + sq.y) % 2 == 0
            sprite_key = (sq.terrain_type, is_dirt)
            screen.blit(square_imgs[sprite_key], (x, y))

            if sq.terrain_type == "m" and sq.owned_by is not None:
                border_color = MINE_WHITE_BORDER if sq.owned_by == 0 else MINE_BLACK_BORDER
                pygame.draw.rect(screen, border_color, (x, y, SQUARE_SIZE, SQUARE_SIZE), 3)

            if sq.has_treasure:
                tw = treasure_img.get_width()
                th = treasure_img.get_height()
                screen.blit(treasure_img, (x + (SQUARE_SIZE - tw) // 2, y + (SQUARE_SIZE - th) // 2))

            if sq.piece_type is not None:
                pkey = (sq.piece_type, sq.piece_is_white)
                screen.blit(piece_imgs[pkey], (x, y))

        # Highlight last move
        if move_index > 0:
            last = moves[move_index - 1]
            highlight_ids = []
            if last[0] == M:
                highlight_ids = [last[1], last[2]]
            else:
                highlight_ids = [last[2]]
            for sid in highlight_ids:
                sq = board.squares[sid]
                x = sq.x * SQUARE_SIZE
                y = (board.size - 1 - sq.y) * SQUARE_SIZE
                overlay = pygame.Surface((SQUARE_SIZE, SQUARE_SIZE), pygame.SRCALPHA)
                overlay.fill(HIGHLIGHT_COLOR)
                screen.blit(overlay, (x, y))

        # Panel
        px = board_px + 10
        pygame.draw.rect(screen, PANEL_BG, (board_px, 0, PANEL_WIDTH, height))

        turn_text = "White's Turn" if board.white_is_active else "Black's Turn"
        txt = info_font.render(turn_text, True, TURN_ACTIVE)
        screen.blit(txt, (px, 15))

        wg = gold_font.render(f"White Gold: {board.white_gold}", True, GOLD_COLOR)
        screen.blit(wg, (px, 50))
        bg = gold_font.render(f"Black Gold: {board.black_gold}", True, GOLD_COLOR)
        screen.blit(bg, (px, 80))

        wm = info_font.render(f"White Mines: {board.mines_owned_by_white}", True, TEXT_COLOR)
        screen.blit(wm, (px, 115))
        bm = info_font.render(f"Black Mines: {board.mines_owned_by_black}", True, TEXT_COLOR)
        screen.blit(bm, (px, 140))

        # Move counter
        counter = info_font.render(f"Move {move_index} / {len(moves)}", True, TEXT_COLOR)
        screen.blit(counter, (px, 180))

        # Result
        res = info_font.render(result_str, True, GOLD_COLOR)
        screen.blit(res, (px, 210))

        # Last move description
        if move_index > 0:
            last = moves[move_index - 1]
            if last[0] == M:
                desc = f"Move: {last[1]} -> {last[2]}"
            else:
                color = "W" if last[3] else "B"
                desc = f"Place: {color} {last[1]} @ {last[2]}"
            mt = info_font.render(desc, True, TEXT_COLOR)
            screen.blit(mt, (px, 245))

        # Controls
        controls = [
            "Right: next move",
            "Left: prev move",
            "Home: start",
            "End: end",
        ]
        for i, c in enumerate(controls):
            ct = help_font.render(c, True, (150, 150, 150))
            screen.blit(ct, (px, height - 90 + i * 20))

        pygame.display.flip()

    draw()

    running = True
    while running:
        for event in pygame.event.get():
            if event.type == pygame.QUIT:
                running = False
                break
            if event.type == pygame.KEYDOWN:
                if event.key == pygame.K_RIGHT and move_index < len(moves):
                    board.do_move(moves[move_index])
                    move_index += 1
                    draw()
                elif event.key == pygame.K_LEFT and move_index > 0:
                    move_index -= 1
                    board = rebuild_board(move_index)
                    draw()
                elif event.key == pygame.K_HOME:
                    move_index = 0
                    board = rebuild_board(0)
                    draw()
                elif event.key == pygame.K_END:
                    move_index = len(moves)
                    board = rebuild_board(move_index)
                    draw()

    pygame.quit()
    sys.exit()


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "--replay":
        run_replay(sys.argv[2])
    else:
        run_ui()
