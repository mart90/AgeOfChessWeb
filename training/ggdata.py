"""Reader for training-data files written by `gg gendata`.

Format: engine/src/traindata.rs (keep in sync) and docs/training.md.

    python training/ggdata.py training/data/file.bin    # summary + sanity checks
"""
import struct
import sys

import numpy as np

MAGIC = b"GGTD"
VERSION = 2          # 2: adds the static eval; version 1 files are still readable
HEADER_SIZE = 32

FLAG_IN_CHECK = 1
FLAG_BEST_TACTICAL = 2
FLAG_RANDOM_MOVE = 4
FLAG_MATE_SCORE = 8

REASONS = {0: "none", 1: "checkmate", 2: "gold", 3: "stalemate"}

# Square byte: bits 0-3 content (0 empty, 1-6 white K Q R B N P, 7-12 black K..P, 13 treasure),
# bits 4-5 terrain (0 open, 1 rocks, 2 forest, 3 mine), bits 6-7 mine owner (0 none, 1 white, 2 black)
TREASURE = 13


def record_dtype(board_size):
    n = board_size * board_size
    return np.dtype([
        ("squares", "u1", (n,)),
        ("side", "u1"),
        ("flags", "u1"),
        ("gold", "<i2", (2,)),      # white, black
        ("ply", "<u2"),
        ("score", "<i2"),           # side to move, 1 gold = 10; mates = ±32000
        ("result", "i1"),           # side to move: +1 win, 0 draw/unfinished, -1 loss
        ("reason", "u1"),
        ("game", "<u4"),
        ("best_from", "u1"),        # 255 for placements
        ("best_to", "u1"),
        ("best_place", "u1"),       # placed kind (engine numbering: 2 Q 3 R 4 B 5 N 6 P), 0 = move
        ("depth", "u1"),
        ("static_eval", "<i2"),     # hand-written eval, no search, side to move (version 2+; 0 in v1)
        ("reserved", "u1", (2,)),
    ])


class GGData:
    """Memory-mapped records of one file (`.records` is a numpy structured array)."""

    def __init__(self, path):
        self.path = path
        with open(path, "rb") as f:
            h = f.read(HEADER_SIZE)
        if h[:4] != MAGIC:
            raise ValueError(f"{path}: not a gendata file")
        version, size, rec_size = struct.unpack("<HHH", h[4:10])
        if version not in (1, 2):
            raise ValueError(f"{path}: version {version}, reader supports 1-{VERSION}")
        self.version = version
        self.has_static_eval = version >= 2
        self.board_size = size
        self.queen_drops = h[10]
        self.queen_cost = h[11]
        self.nodes = struct.unpack("<I", h[12:16])[0]
        dtype = record_dtype(size)
        if dtype.itemsize != rec_size:
            raise ValueError(f"{path}: record size {rec_size}, reader expects {dtype.itemsize}")
        # A run that was killed (e.g. a reclaimed spot instance) can leave a partial last record
        import os
        count = (os.path.getsize(path) - HEADER_SIZE) // rec_size
        self.truncated = (os.path.getsize(path) - HEADER_SIZE) % rec_size != 0
        self.records = np.memmap(path, dtype=dtype, mode="r", offset=HEADER_SIZE, shape=(count,))

    def __len__(self):
        return len(self.records)


def gold_threshold(board_size):
    return 150 + 25 * (board_size - 6) // 2


def summary(path):
    d = GGData(path)
    r = d.records
    n = len(r)
    games = np.unique(r["game"])
    print(f"{path}: {n:,} positions, {len(games):,} games, board {d.board_size}x{d.board_size}, "
          f"{d.nodes:,} nodes/move, queen cost {d.queen_cost}, drop mask {d.queen_drops}")
    if n == 0:
        return

    # Sanity checks
    content = r["squares"] & 15
    kings_w = (content == 1).sum(axis=1)
    kings_b = (content == 7).sum(axis=1)
    problems = []
    if not ((kings_w == 1) & (kings_b == 1)).all():
        problems.append(f"{((kings_w != 1) | (kings_b != 1)).sum()} positions without exactly one king per side")
    if (content > TREASURE).any():
        problems.append("invalid square content codes")
    first_ply = r["ply"][np.r_[True, r["game"][1:] != r["game"][:-1]]]
    if (first_ply != 0).any():
        problems.append(f"{(first_ply != 0).sum()} games not starting at ply 0")
    print("  sanity:", "OK" if not problems else "; ".join(problems))

    flags = r["flags"]
    pct = lambda m: f"{100 * m.mean():.1f}%"
    print(f"  in check {pct(flags & FLAG_IN_CHECK > 0)}, best move tactical {pct(flags & FLAG_BEST_TACTICAL > 0)}, "
          f"random move {pct(flags & FLAG_RANDOM_MOVE > 0)}, mate score {pct(flags & FLAG_MATE_SCORE > 0)}")
    ends = r[np.r_[r["game"][1:] != r["game"][:-1], True]]
    reasons = {REASONS[k]: int((ends["reason"] == k).sum()) for k in REASONS}
    white_won = ((ends["result"] == 1) & (ends["side"] == 0)) | ((ends["result"] == -1) & (ends["side"] == 1))
    print(f"  results: White {100 * white_won.mean():.1f}%, reasons {reasons}")
    print(f"  plies/game {n / len(games):.1f}, avg depth {r['depth'].mean():.2f}, "
          f"score |median| {np.median(np.abs(r['score'])) / 10:.1f} gold")


if __name__ == "__main__":
    for p in sys.argv[1:]:
        summary(p)
