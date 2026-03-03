"""Generate self-play games using the current best model and save selected ones for review."""
import argparse
import json
import os
import torch

from model import PolicyNetwork
from generate_boards import fetch_boards
from self_play import (
    check_victory, pick_random_move, policy_move, MAX_MOVES,
)
from board import M, P


def serialize_board(board):
    """Serialize initial board state to JSON-compatible dict."""
    squares = []
    for sq in board.squares:
        s = {"id": sq.id, "x": sq.x, "y": sq.y}
        if sq.terrain_type is not None:
            s["terrain"] = sq.terrain_type
        if sq.piece_type is not None:
            s["piece"] = sq.piece_type
            s["isWhite"] = sq.piece_is_white
        if sq.has_treasure:
            s["treasure"] = True
        if sq.owned_by is not None:
            s["ownedBy"] = sq.owned_by
        squares.append(s)
    return {
        "size": board.size,
        "squares": squares,
        "whiteGold": board.white_gold,
        "blackGold": board.black_gold,
    }


def serialize_move(move):
    """Serialize a move tuple to JSON-compatible dict."""
    if move[0] == M:
        return {"type": "move", "from": move[1], "to": move[2]}
    else:
        return {"type": "place", "piece": move[1], "dest": move[2], "isWhite": move[3]}


def play_and_record(board, white_model=None, black_model=None, device=None, white_temp=0.5, black_temp=0.5):
    """Play a full game, recording every move (both sides)."""
    game_board = board.clone()
    moves = []
    move_count = 0

    while move_count < MAX_MOVES:
        result = check_victory(game_board)
        if result is not None:
            return moves, result

        legal_moves = game_board.get_legal_moves()
        if len(legal_moves) == 0:
            result = -1 if game_board.white_is_active else 1
            return moves, result

        # Choose model based on active player
        current_model = white_model if game_board.white_is_active else black_model
        current_temp = white_temp if game_board.white_is_active else black_temp

        if current_model is not None:
            move = policy_move(current_model, game_board, legal_moves, device, current_temp)
        else:
            move = pick_random_move(legal_moves, placement_bias=1.0)

        moves.append(serialize_move(move))
        game_board.do_move(move)
        move_count += 1

    return moves, 0


def main():
    parser = argparse.ArgumentParser(description="Generate and save self-play games for review")
    parser.add_argument("--games", type=int, default=50, help="Number of games to play")
    parser.add_argument("--save", type=int, default=5, help="Number of games to save as replays")
    parser.add_argument("--model", type=str, default="checkpoints/best_overall.pt",
                        help="Path to model checkpoint for both sides (omit for random play)")
    parser.add_argument("--white-model", type=str, help="Path to white's model (overrides --model)")
    parser.add_argument("--black-model", type=str, help="Path to black's model (overrides --model)")
    parser.add_argument("--random", action="store_true", help="Use random play for both sides")
    parser.add_argument("--white-random", action="store_true", help="Use random play for white")
    parser.add_argument("--black-random", action="store_true", help="Use random play for black")
    parser.add_argument("--temperature", type=float, default=0.5, help="Temperature for both sides")
    parser.add_argument("--white-temp", type=float, help="Temperature for white (overrides --temperature)")
    parser.add_argument("--black-temp", type=float, help="Temperature for black (overrides --temperature)")
    args = parser.parse_args()

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")

    # Determine white model
    white_model_path = args.white_model or args.model
    white_model = None
    if not (args.random or args.white_random) and os.path.exists(white_model_path):
        white_model = PolicyNetwork().to(device)
        white_model.load_state_dict(torch.load(white_model_path, map_location=device))
        white_model.eval()
        print(f"White: {white_model_path}")
    else:
        print("White: random play")

    # Determine black model
    black_model_path = args.black_model or args.model
    black_model = None
    if not (args.random or args.black_random) and os.path.exists(black_model_path):
        black_model = PolicyNetwork().to(device)
        black_model.load_state_dict(torch.load(black_model_path, map_location=device))
        black_model.eval()
        print(f"Black: {black_model_path}")
    else:
        print("Black: random play")

    # Determine temperatures
    white_temp = args.white_temp if args.white_temp is not None else args.temperature
    black_temp = args.black_temp if args.black_temp is not None else args.temperature
    print(f"Temperatures: White={white_temp}, Black={black_temp}")

    print(f"\nFetching {args.games} boards from server...")
    boards = fetch_boards(amount=args.games)
    print(f"Fetched {len(boards)} boards\n")

    os.makedirs("replays", exist_ok=True)
    saved = 0

    for i in range(1, args.games + 1):
        board = boards[i - 1]
        moves, result = play_and_record(
            board,
            white_model=white_model,
            black_model=black_model,
            device=device,
            white_temp=white_temp,
            black_temp=black_temp
        )

        result_str = {1: "White wins", -1: "Black wins", 0: "Draw"}[result]
        print(f"Game {i:2d}: {len(moves)} moves, {result_str}")

        if saved < args.save:
            game_data = {
                "gameNumber": i,
                "initialBoard": serialize_board(boards[i - 1]),
                "moves": moves,
                "result": result,
                "totalMoves": len(moves),
            }
            path = os.path.join("replays", f"game_{i:02d}.json")
            with open(path, "w") as f:
                json.dump(game_data, f, indent=2)
            print(f"  -> Saved to {path}")
            saved += 1

    print(f"\nDone! Saved {saved} replays.")
    print("Run: python ui.py --replay replays/game_01.json")


if __name__ == "__main__":
    main()
