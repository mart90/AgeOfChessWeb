import numpy as np
from board import M, P

# Piece type → plane offset (within white or black group)
PIECE_PLANE = {"k": 0, "q": 1, "r": 2, "b": 3, "n": 4, "p": 5}

# Piece type → placement index for move encoding
PLACEMENT_INDEX = {"q": 0, "r": 1, "b": 2, "n": 3, "p": 4}
PLACEMENT_TYPES = ["q", "r", "b", "n", "p"]

NUM_PLANES = 21
BOARD_SIZE = 10
NUM_SQUARES = BOARD_SIZE * BOARD_SIZE
NUM_MOVE_ACTIONS = NUM_SQUARES * NUM_SQUARES           # 10000
NUM_PLACEMENT_ACTIONS = len(PLACEMENT_TYPES) * NUM_SQUARES  # 500
NUM_ACTIONS = NUM_MOVE_ACTIONS + NUM_PLACEMENT_ACTIONS  # 10500


def encode_board(board):
    """Encode a Board object as a (21, size, size) float32 numpy array."""
    size = board.size
    planes = np.zeros((NUM_PLANES, size, size), dtype=np.float32)

    for sq in board.squares:
        x, y = sq.x, sq.y
        # Piece planes (0-11)
        if sq.piece_type is not None:
            offset = 0 if sq.piece_is_white else 6
            planes[offset + PIECE_PLANE[sq.piece_type], y, x] = 1.0
        # Treasure (12)
        if sq.has_treasure:
            planes[12, y, x] = 1.0
        # Terrain (13-15)
        if sq.terrain_type == "m":
            planes[13, y, x] = 1.0
        elif sq.terrain_type == "r":
            planes[14, y, x] = 1.0
        elif sq.terrain_type == "t":
            planes[15, y, x] = 1.0
        # Mine ownership (16-17)
        if sq.terrain_type == "m":
            if sq.owned_by == 0:
                planes[16, y, x] = 1.0  # white-owned
            elif sq.owned_by == 1:
                planes[17, y, x] = 1.0  # black-owned

    # Active player (18)
    planes[18, :, :] = 1.0 if board.white_is_active else 0.0
    # Gold (19-20), normalized
    planes[19, :, :] = board.white_gold / 500.0
    planes[20, :, :] = board.black_gold / 500.0

    return planes


def move_to_index(move):
    """Convert a move tuple to an action index (0–10499)."""
    if move[0] == M:
        # (M, source_id, dest_id)
        return move[1] * NUM_SQUARES + move[2]
    else:
        # (P, piece_type, dest_id, is_white)
        piece_idx = PLACEMENT_INDEX[move[1]]
        return NUM_MOVE_ACTIONS + piece_idx * NUM_SQUARES + move[2]


def index_to_move(index, is_white=True):
    """Convert an action index back to a move tuple."""
    if index < NUM_MOVE_ACTIONS:
        from_id = index // NUM_SQUARES
        to_id = index % NUM_SQUARES
        return (M, from_id, to_id)
    else:
        idx = index - NUM_MOVE_ACTIONS
        piece_idx = idx // NUM_SQUARES
        dest_id = idx % NUM_SQUARES
        return (P, PLACEMENT_TYPES[piece_idx], dest_id, is_white)


def get_legal_move_mask(legal_moves):
    """Return a float32 array of shape (NUM_ACTIONS,) with 0.0 for legal, -1e9 for illegal."""
    mask = np.full(NUM_ACTIONS, -1e9, dtype=np.float32)
    for move in legal_moves:
        mask[move_to_index(move)] = 0.0
    return mask


def augment_180(encoded, move_idx):
    """
    Rotate position 180° and swap colors.
    Returns (augmented_encoded, augmented_move_idx).
    """
    aug = encoded.copy()

    # Flip spatial dimensions (180° rotation)
    aug = np.flip(np.flip(aug, axis=1), axis=2).copy()

    # Swap white/black piece planes (0-5 ↔ 6-11)
    white_planes = aug[0:6].copy()
    aug[0:6] = aug[6:12]
    aug[6:12] = white_planes

    # Swap mine ownership planes (16 ↔ 17)
    mine_white = aug[16].copy()
    aug[16] = aug[17]
    aug[17] = mine_white

    # Flip active player
    aug[18] = 1.0 - aug[18]

    # Swap gold planes (19 ↔ 20)
    gold_white = aug[19].copy()
    aug[19] = aug[20]
    aug[20] = gold_white

    # Remap move index (flip square IDs: id → 99 - id)
    if move_idx < NUM_MOVE_ACTIONS:
        from_id = move_idx // NUM_SQUARES
        to_id = move_idx % NUM_SQUARES
        aug_idx = (NUM_SQUARES - 1 - from_id) * NUM_SQUARES + (NUM_SQUARES - 1 - to_id)
    else:
        offset = move_idx - NUM_MOVE_ACTIONS
        piece_idx = offset // NUM_SQUARES
        dest_id = offset % NUM_SQUARES
        aug_idx = NUM_MOVE_ACTIONS + piece_idx * NUM_SQUARES + (NUM_SQUARES - 1 - dest_id)

    return aug, aug_idx
