"""Network inputs from gendata records (feature set v1). See docs/training.md.

Sparse features are computed per perspective: the side to move ("us") and the opponent
("them") each get their own list, built with the same table, so one weight matrix serves both
(the network sees the position "from both sides"). Per square, at most two features fire:

    block  0-5   own   K Q R B N P          block 12  treasure
    block  6-11  enemy K Q R B N P          block 13  rocks
                                            block 14  forest
                                            block 15  mine, unowned
                                            block 16  mine, owned by this perspective
                                            block 17  mine, owned by the other side
    feature index = block * N*N + square

Dense inputs (side to move first): gold / gold-victory threshold for us and them, owned mines / 10
for us and them.

The rules don't depend on board orientation (pawns move and capture in all four directions), so
all 8 rotations/reflections of a position are equally valid: `symmetry` picks one (0 = none).
"""
import numpy as np

NUM_BLOCKS = 18
NUM_DENSE = 4


def num_features(board_size):
    return NUM_BLOCKS * board_size * board_size


def symmetry_perms(board_size):
    """perm[s][new_square] = old_square for the 8 symmetries of the square board."""
    n = board_size
    ys, xs = np.divmod(np.arange(n * n), n)
    m = n - 1
    maps = [
        (xs, ys), (m - ys, xs), (m - xs, m - ys), (ys, m - xs),     # rotations
        (m - xs, ys), (xs, m - ys), (ys, xs), (m - ys, m - xs),     # reflections
    ]
    return [(oy * n + ox).astype(np.int64) for ox, oy in maps]


_PERMS = {}


def encode(records, symmetry=0):
    """records: structured array (see ggdata.record_dtype).
    Returns (us_idx, us_offsets, them_idx, them_offsets, dense) as numpy arrays, ready for
    torch.nn.EmbeddingBag(mode="sum") with offsets."""
    squares = records["squares"]
    b, nn = squares.shape
    n = int(round(nn ** 0.5))
    if symmetry:
        if n not in _PERMS:
            _PERMS[n] = symmetry_perms(n)
        squares = squares[:, _PERMS[n][symmetry]]

    content = (squares & 15).astype(np.int64)
    terrain = (squares >> 4) & 3
    owner = (squares >> 6).astype(np.int64)            # 0 none, 1 white, 2 black
    white_to_move = (records["side"] == 0)[:, None]

    is_white = (content >= 1) & (content <= 6)
    is_black = (content >= 7) & (content <= 12)
    kind = np.where(is_white, content - 1, content - 7)  # 0..5 = K Q R B N P
    own = np.where(white_to_move, is_white, is_black)
    enemy = np.where(white_to_move, is_black, is_white)
    treasure = content == 13

    us_color = np.where(white_to_move, 1, 2)
    them_color = 3 - us_color
    mine = terrain == 3
    mine_block_us = np.where(owner == 0, 15, np.where(owner == us_color, 16, 17))
    mine_block_them = np.where(owner == 0, 15, np.where(owner == them_color, 16, 17))
    terrain_block = np.where(terrain == 1, 13, np.where(terrain == 2, 14, -1))

    sq = np.arange(nn)[None, :]

    def features(own_mask, enemy_mask, mine_block):
        piece_block = np.where(own_mask, kind, np.where(enemy_mask, 6 + kind, np.where(treasure, 12, -1)))
        land_block = np.where(mine, mine_block, terrain_block)
        both = np.concatenate([piece_block, land_block], axis=1)      # (b, 2*nn)
        squares2 = np.concatenate([sq, sq], axis=1).repeat(b, axis=0)
        active = both >= 0
        idx = (both * nn + squares2)[active]
        counts = active.sum(axis=1)
        offsets = np.concatenate([[0], np.cumsum(counts)[:-1]])
        return idx.astype(np.int64), offsets.astype(np.int64)

    us_idx, us_off = features(own, enemy, mine_block_us)
    them_idx, them_off = features(enemy, own, mine_block_them)

    gold = records["gold"].astype(np.float32)               # (b, 2) white, black
    threshold = 150 + 25 * (n - 6) // 2
    stm = records["side"].astype(np.int64)
    gold_us = gold[np.arange(b), stm]
    gold_them = gold[np.arange(b), 1 - stm]
    mines_us = (mine & (owner == us_color)).sum(axis=1)
    mines_them = (mine & (owner == them_color)).sum(axis=1)
    dense = np.stack([gold_us / threshold, gold_them / threshold, mines_us / 10.0, mines_them / 10.0], axis=1)
    return us_idx, us_off, them_idx, them_off, dense.astype(np.float32)
