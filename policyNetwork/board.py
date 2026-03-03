from constants import PIECE_COSTS
from math import sqrt

# Move types
M = 0  # piece move
P = 1  # piece placement

# Move tuple indices: (type, source_id, dest_id) for moves, (type, piece_type, dest_id, is_white) for placements


def _build_neighbor_table(size):
    """Precompute neighbors[square_id][direction] -> square_id or -1."""
    n = size * size
    table = [[-1] * 8 for _ in range(n)]
    for sq_id in range(n):
        x = sq_id % size
        y = sq_id // size
        # Direction 0=N, 1=NE, 2=E, 3=SE, 4=S, 5=SW, 6=W, 7=NW
        candidates = [
            (x, y + 1),      # 0: N
            (x + 1, y + 1),  # 1: NE
            (x + 1, y),      # 2: E
            (x + 1, y - 1),  # 3: SE
            (x, y - 1),      # 4: S
            (x - 1, y - 1),  # 5: SW
            (x - 1, y),      # 6: W
            (x - 1, y + 1),  # 7: NW
        ]
        for d, (nx, ny) in enumerate(candidates):
            if 0 <= nx < size and 0 <= ny < size:
                table[sq_id][d] = ny * size + nx
    return table


def _build_knight_table(size):
    """Precompute knight_jumps[square_id][direction] -> square_id or -1."""
    n = size * size
    table = [[-1] * 8 for _ in range(n)]
    for sq_id in range(n):
        x = sq_id % size
        y = sq_id // size
        # Direction 0=2N+1E, 1=1N+2E, 2=1S+2E, 3=2S+1E, 4=2S+1W, 5=1S+2W, 6=1N+2W, 7=2N+1W
        candidates = [
            (x + 1, y + 2),
            (x + 2, y + 1),
            (x + 2, y - 1),
            (x + 1, y - 2),
            (x - 1, y - 2),
            (x - 2, y - 1),
            (x - 2, y + 1),
            (x - 1, y + 2),
        ]
        for d, (nx, ny) in enumerate(candidates):
            if 0 <= nx < size and 0 <= ny < size:
                table[sq_id][d] = ny * size + nx
    return table


class Board(object):
    def __init__(self, squares):
        self.squares = squares
        self.size = int(sqrt(len(squares)))
        self.white_is_active = True
        self.white_gold = 0
        self.black_gold = 6
        self.white_king_square = self.king_square(True)
        self.black_king_square = self.king_square(False)
        self.mines_owned_by_white = 0
        self.mines_owned_by_black = 0
        self._neighbors = _build_neighbor_table(self.size)
        self._knight_jumps = _build_knight_table(self.size)

    def clone(self):
        """Create an independent copy of this board."""
        new_squares = [sq.clone() for sq in self.squares]
        b = Board.__new__(Board)
        b.squares = new_squares
        b.size = self.size
        b.white_is_active = self.white_is_active
        b.white_gold = self.white_gold
        b.black_gold = self.black_gold
        b.mines_owned_by_white = self.mines_owned_by_white
        b.mines_owned_by_black = self.mines_owned_by_black
        b._neighbors = self._neighbors  # shared (immutable)
        b._knight_jumps = self._knight_jumps  # shared (immutable)
        b.white_king_square = new_squares[self.white_king_square.id] if self.white_king_square else None
        b.black_king_square = new_squares[self.black_king_square.id] if self.black_king_square else None
        return b

    def get_legal_moves(self):
        moves = []
        piece_squares = self.active_piece_squares()
        for square in piece_squares:
            if square.piece_type == "k":
                moves.extend(self.legal_king_moves(square))
            elif square.piece_type == "q":
                moves.extend(self.legal_queen_moves(square))
            elif square.piece_type == "r":
                moves.extend(self.legal_rook_moves(square))
            elif square.piece_type == "b":
                moves.extend(self.legal_bishop_moves(square))
            elif square.piece_type == "n":
                moves.extend(self.legal_knight_moves(square))
            elif square.piece_type == "p":
                moves.extend(self.legal_pawn_moves(square))
        moves.extend(self.legal_piece_placements(piece_squares))
        return moves

    def legal_king_moves(self, source_square):
        legal_moves = []
        neighbors = self._neighbors[source_square.id]
        for direction in range(8):
            dest_id = neighbors[direction]
            if dest_id == -1:
                continue
            dest_square = self.squares[dest_id]
            if dest_square.terrain_type == "r" or dest_square.piece_is_white == self.white_is_active:
                continue
            move = (M, source_square.id, dest_id)
            if self.test_move(move, self.white_is_active):
                legal_moves.append(move)
        return legal_moves

    def legal_queen_moves(self, source_square):
        legal_moves = []
        for direction in range(8):
            for sq in self.open_squares_vector(source_square, direction):
                move = (M, source_square.id, sq.id)
                if self.test_move(move, self.white_is_active):
                    legal_moves.append(move)
        return legal_moves

    def legal_rook_moves(self, source_square):
        legal_moves = []
        for direction in range(0, 8, 2):
            for sq in self.open_squares_vector(source_square, direction):
                move = (M, source_square.id, sq.id)
                if self.test_move(move, self.white_is_active):
                    legal_moves.append(move)
        return legal_moves

    def legal_bishop_moves(self, source_square):
        legal_moves = []
        for direction in range(1, 8, 2):
            for sq in self.open_squares_vector(source_square, direction):
                move = (M, source_square.id, sq.id)
                if self.test_move(move, self.white_is_active):
                    legal_moves.append(move)
        return legal_moves

    def legal_knight_moves(self, source_square):
        possible_moves = []
        knight_jumps = self._knight_jumps[source_square.id]
        for direction in range(8):
            dest_id = knight_jumps[direction]
            if dest_id == -1:
                continue
            dest_square = self.squares[dest_id]
            if dest_square.terrain_type == "r" or dest_square.piece_is_white == self.white_is_active:
                continue
            possible_moves.append((M, source_square.id, dest_id))
        legal_moves = []
        for move in possible_moves:
            if self.test_move(move, self.white_is_active):
                legal_moves.append(move)
        return legal_moves

    def legal_pawn_moves(self, source_square):
        possible_moves = []
        neighbors = self._neighbors[source_square.id]
        for direction in range(8):
            dest_id = neighbors[direction]
            if dest_id == -1:
                continue
            dest_square = self.squares[dest_id]
            if dest_square.terrain_type == "r" or dest_square.piece_is_white == self.white_is_active:
                continue
            if direction % 2 == 0 and (dest_square.piece_type is not None or dest_square.has_treasure):
                continue
            elif direction % 2 == 1 and dest_square.piece_type is None and not dest_square.has_treasure:
                continue
            possible_moves.append((M, source_square.id, dest_id))
        legal_moves = []
        for move in possible_moves:
            if self.test_move(move, self.white_is_active):
                legal_moves.append(move)
        return legal_moves

    def legal_piece_placements(self, piece_squares):
        possible_placements = []
        legal_placements = []
        for piece in self.affordable_pieces():
            squares_visited = []
            king_square = self.active_king_square()
            neighbors = self._neighbors[king_square.id]
            for direction in range(8):
                dest_id = neighbors[direction]
                if dest_id == -1:
                    continue
                dest_square = self.squares[dest_id]
                if dest_square.terrain_type == "r" or dest_square.piece_type is not None or dest_square.has_treasure:
                    continue
                possible_placements.append((P, piece, dest_id, self.white_is_active))
                squares_visited.append(dest_id)
            if piece == "p":
                for piece_square in piece_squares:
                    if piece_square.piece_type == "p" or piece_square.piece_type == "k":
                        continue
                    ps_neighbors = self._neighbors[piece_square.id]
                    for direction in range(8):
                        dest_id = ps_neighbors[direction]
                        if dest_id == -1 or dest_id in squares_visited:
                            continue
                        dest_square = self.squares[dest_id]
                        if dest_square.terrain_type == "r" or dest_square.piece_type is not None or dest_square.has_treasure:
                            continue
                        possible_placements.append((P, "p", dest_id, self.white_is_active))
                        squares_visited.append(dest_id)

        for placement in possible_placements:
            if self.test_move(placement, self.white_is_active):
                legal_placements.append(placement)
        return legal_placements

    # Returns false if our king is in check afterwards
    def test_move(self, move, is_white):
        if move[0] == M:
            source_id = move[1]
            dest_id = move[2]
        else:
            dest_id = move[2]

        dest_square = self.squares[dest_id]
        has_treasure = dest_square.has_treasure
        captured_piece = dest_square.piece_type
        captured_is_white = dest_square.piece_is_white
        if move[0] == M:
            source_square = self.squares[source_id]
            dest_square.piece_type = source_square.piece_type
            dest_square.piece_is_white = source_square.piece_is_white
            if has_treasure:
                dest_square.has_treasure = False
            source_square.piece_type = None
            source_square.piece_is_white = None
            if dest_square.piece_type == "k":
                if is_white:
                    self.white_king_square = dest_square
                else:
                    self.black_king_square = dest_square
        else: # Piece placement
            dest_square.piece_type = move[1]
            dest_square.piece_is_white = move[3]

        our_king = self.white_king_square if is_white else self.black_king_square
        in_check = self.is_capturable(our_king, not is_white)

        # Undo move
        if move[0] == M:
            source_square.piece_type = dest_square.piece_type
            source_square.piece_is_white = dest_square.piece_is_white
            if captured_piece is not None:
                dest_square.piece_type = captured_piece
                dest_square.piece_is_white = captured_is_white
            else:
                dest_square.piece_type = None
                dest_square.piece_is_white = None
            if has_treasure:
                dest_square.has_treasure = True
            if source_square.piece_type == "k":
                if is_white:
                    self.white_king_square = source_square
                else:
                    self.black_king_square = source_square
        else: # Piece placement
            dest_square.piece_type = None
            dest_square.piece_is_white = None
        return not in_check

    def do_move(self, move):
        if move[0] == M:
            dest_id = move[2]
            source_id = move[1]
        else:
            dest_id = move[2]

        dest_square = self.squares[dest_id]
        if move[0] == M:
            source_square = self.squares[source_id]
            dest_square.piece_type = source_square.piece_type
            dest_square.piece_is_white = source_square.piece_is_white
            if dest_square.has_treasure:
                if self.white_is_active:
                    self.white_gold += 20
                else:
                    self.black_gold += 20
                dest_square.has_treasure = False
            source_square.piece_type = None
            source_square.piece_is_white = None
            if dest_square.piece_type == "k":
                if self.white_is_active:
                    self.white_king_square = dest_square
                else:
                    self.black_king_square = dest_square
        else: # Piece placement
            dest_square.piece_type = move[1]
            dest_square.piece_is_white = move[3]
            cost = next(pc["cost"] for pc in PIECE_COSTS if pc["type"] == move[1])
            if self.white_is_active:
                self.white_gold -= cost
            else:
                self.black_gold -= cost

        if dest_square.terrain_type == "m" and not ((dest_square.owned_by == 0 and self.white_is_active) or (dest_square.owned_by == 1 and not self.white_is_active)):
            if dest_square.owned_by == 1 and self.white_is_active:
                self.mines_owned_by_black -= 1
            elif dest_square.owned_by == 0 and not self.white_is_active:
                self.mines_owned_by_white -= 1
            dest_square.owned_by = 0 if self.white_is_active else 1
            if self.white_is_active:
                self.mines_owned_by_white += 1
            else:
                self.mines_owned_by_black += 1

        self.white_is_active = not self.white_is_active
        self.income()

    def income(self):
        if self.white_is_active:
            self.white_gold += 1 + self.mines_owned_by_white * 3
        else:
            self.black_gold += 1 + self.mines_owned_by_black * 3

    def active_piece_squares(self):
        return [s for s in self.squares if s.piece_type is not None and s.piece_is_white == self.white_is_active]

    def affordable_pieces(self):
        gold = self.active_player_gold()
        return [pc["type"] for pc in PIECE_COSTS if pc["cost"] <= gold]

    def active_player_gold(self):
        return self.white_gold if self.white_is_active else self.black_gold

    def is_capturable(self, square, capturable_by_white):
        for direction in range(8):
            if self.discover_attacker_vector(square, direction, not capturable_by_white):
                return True
        knight_jumps = self._knight_jumps[square.id]
        for direction in range(8):
            dest_id = knight_jumps[direction]
            if dest_id == -1:
                continue
            sq = self.squares[dest_id]
            if sq.piece_type == "n" and sq.piece_is_white == capturable_by_white:
                return True
        return False

    def king_square(self, is_white):
        return [s for s in self.squares if s.piece_type == "k" and s.piece_is_white == is_white][0]

    def active_king_square(self):
        return self.white_king_square if self.white_is_active else self.black_king_square

    # Direction 0 is north, then it continues clockwise
    # Returns true if we discover an enemy piece that could capture the source square
    def discover_attacker_vector(self, source_square, direction, defender_is_white):
        sq_id = source_square.id
        dist = 1
        neighbors = self._neighbors
        squares = self.squares
        while True:
            dest_id = neighbors[sq_id][direction]
            if dest_id == -1:
                return False
            new_square = squares[dest_id]
            blocker_type = new_square.blocker_type(defender_is_white)
            if blocker_type == 1:
                return False
            if blocker_type == 2:
                pt = new_square.piece_type
                if pt is None or pt == "n":
                    return False
                if pt == "k":
                    return dist == 1
                if pt == "q":
                    return True
                if pt == "r":
                    return direction % 2 == 0
                if pt == "b":
                    return direction % 2 == 1
                if pt == "p":
                    return direction % 2 == 1 and dist == 1
                return False
            sq_id = dest_id
            dist += 1

    # Direction 0 is north, then it continues clockwise
    # Returns all squares a piece of ours could move to from the given source square, going the given direction
    def open_squares_vector(self, source_square, direction):
        legal_squares = []
        sq_id = source_square.id
        neighbors = self._neighbors
        squares = self.squares
        while True:
            dest_id = neighbors[sq_id][direction]
            if dest_id == -1:
                return legal_squares
            dest_square = squares[dest_id]
            blocker_type = dest_square.blocker_type(self.white_is_active)
            if blocker_type == 1:
                return legal_squares
            legal_squares.append(dest_square)
            if blocker_type == 2:
                return legal_squares
            sq_id = dest_id
