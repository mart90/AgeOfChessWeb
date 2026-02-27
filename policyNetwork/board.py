from constants import PIECE_COSTS
from math import sqrt


class Board(object):
    def __init__(self, squares):
        self.squares = squares
        self.size = int(sqrt(len(squares)))
        self.white_is_active = True
        self.white_gold = 0
        self.black_gold = 5
        self.white_king_square = None
        self.black_king_square = None

    def get_legal_moves(self):
        moves = []
        piece_squares = self.get_piece_squares()
        for square in piece_squares:
            if square.piece_type == "k":
                moves.append(self.legal_king_moves(square))

    def legal_king_moves(self, source_square):
        moves = []
        for direction in range(0, 7):
            dest_square = self.square_in_direction(source_square, direction)
            if dest_square is None or dest_square.terrain_type == "r" or self.is_capturable(dest_square, not self.white_is_active):
                continue
            moves.append({
                "type": "m",
                "source_id": source_square.id,
                "dest_id": dest_square.id
            })
        return moves

    def legal_queen_moves(self, source_square):
        moves = []
        for direction in range(0, 7):
            moves.append(self.legal_squares_vector(source_square, direction))
        return moves
        
    def piece_squares(self):
        return [s for s in self.squares if s.piece_type is not None]
    
    def affordable_pieces(self):
        gold = self.active_player_gold()
        return [pc["type"] for pc in PIECE_COSTS if pc["cost"] <= gold]
    
    def active_player_gold(self):
        return self.white_gold if self.white_is_active else self.black_gold
    
    def is_capturable(self, square, capturable_by_white):
        for direction in range(0, 7):
            if self.discover_attacker_vector(square, direction, not capturable_by_white):
                return True
        for direction in range(0, 7):
            new_square = self.knight_jump(square, direction)
            if new_square is None:
                continue
            if new_square.piece_type == "n" and new_square.piece_is_white == capturable_by_white:
                return True
        return False

    def king_square(self, is_white):
        return [s for s in self.squares if s.piece_type == "k" and s.is_white == is_white][0]
    
    # Direction 0 is north, then it continues clockwise
    # Returns true if we discover an enemy piece that could capture the source square
    def discover_attacker_vector(self, source_square, direction, defender_is_white):
        square = source_square
        range = 1
        while True:
            new_square = self.square_in_direction(square, direction)
            if new_square is None:
                return False
            blocker_type = new_square.blocker_type(defender_is_white)
            if blocker_type == 1:
                return False
            if blocker_type == 2:
                if new_square.piece_type is None or new_square.piece_type == "n":
                    return False
                if new_square.piece_type == "k":
                    return range == 1
                return True if new_square.piece_type == "q" \
                    else True if new_square.piece_type == "r" and direction % 2 == 0 \
                    else True if new_square.piece_type == "b" and direction % 2 == 1 \
                    else False
            square = new_square
            range += 1
    
    # Direction 0 is north, then it continues clockwise
    # Returns all squares a piece of ours could move to from the given source square, going the given direction
    def open_squares_vector(self, source_square, direction, is_white):
        legal_squares = []
        square = source_square
        while True:
            dest_square = self.square_in_direction(square, direction)
            if dest_square is None:
                return legal_squares
            blocker_type = dest_square.blocker_type(is_white)
            if blocker_type == 1:
                return legal_squares
            legal_squares.append(dest_square)
            if blocker_type == 2:
                return legal_squares
            square = dest_square

    # Direction 0 is north, then it continues clockwise
    # Returns None if we went off the board
    def square_in_direction(self, source_square, direction):
        source_square_id = source_square.y * self.size + source_square.x

        if direction == 0:
            dest_square_id = source_square_id + self.size
            if dest_square_id >= len(self.squares):
                return None
        elif direction == 1:
            dest_square_id = source_square_id + self.size + 1
            if dest_square_id >= len(self.squares) or dest_square_id % self.size == 0:
                return None
        elif direction == 2:
            dest_square_id = source_square_id + 1
            if dest_square_id % self.size == 0:
                return None
        elif direction == 3:
            dest_square_id = source_square_id - self.size + 1
            if dest_square_id < 0 or dest_square_id % self.size == 0:
                return None
        elif direction == 4:
            dest_square_id = source_square_id - self.size
            if dest_square_id < 0:
                return None
        elif direction == 5:
            dest_square_id = source_square_id - self.size - 1
            if dest_square_id < 0 or (dest_square_id + 1) % self.size == 0:
                return None
        elif direction == 6:
            dest_square_id = source_square_id - 1
            if (dest_square_id + 1) % self.size == 0:
                return None
        elif direction == 7:
            dest_square_id = source_square_id + self.size - 1
            if dest_square_id >= len(self.squares) or (dest_square_id + 1) % self.size == 0:
                return None
            
        return self.squares[dest_square_id]
    
    # Direction 0 is 2 squares up and 1 to the right, then it continues clockwise
    # Returns None if we went off the board
    def knight_jump(self, source_square, direction):
        source_square_id = source_square.y * self.size + source_square.x

        if direction == 0:
            dest_square_id = source_square_id + self.size * 2 + 1
            if dest_square_id >= len(self.squares) or dest_square_id % self.size == 0:
                return None
        elif direction == 1:
            dest_square_id = source_square_id + self.size + 2
            if dest_square_id >= len(self.squares) or dest_square_id % self.size in (0, 1):
                return None
        elif direction == 2:
            dest_square_id = source_square_id - self.size + 2
            if dest_square_id < 0 or dest_square_id % self.size in (0, 1):
                return None
        elif direction == 3:
            dest_square_id = source_square_id - self.size * 2 + 1
            if dest_square_id < 0 or dest_square_id % self.size == 0:
                return None
        elif direction == 4:
            dest_square_id = source_square_id - self.size * 2 - 1
            if dest_square_id < 0 or (dest_square_id + 1) % self.size == 0:
                return None
        elif direction == 5:
            dest_square_id = source_square_id - self.size - 2
            if dest_square_id < 0 or (dest_square_id + 2) % self.size in (0, 1):
                return None
        elif direction == 6:
            dest_square_id = source_square_id + self.size - 2
            if dest_square_id >= len(self.squares) or (dest_square_id + 2) % self.size in (0, 1):
                return None
        elif direction == 7:
            dest_square_id = source_square_id + self.size * 2 - 1
            if dest_square_id >= len(self.squares) or (dest_square_id + 1) % self.size == 0:
                return None
            
        return self.squares[dest_square_id]
