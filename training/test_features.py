"""Checks for the feature encoder:  python training/test_features.py [data.bin]"""
import sys

import numpy as np

from features import NUM_BLOCKS, encode, symmetry_perms
from ggdata import GGData


def swap_colors(rec):
    """Same position with White and Black exchanged (pieces, mine owners, gold, side to move)."""
    r = rec.copy()
    sq = r["squares"]
    content = sq & 15
    terrain = sq & 0x30
    owner = sq >> 6
    white = (content >= 1) & (content <= 6)
    black = (content >= 7) & (content <= 12)
    content = np.where(white, content + 6, np.where(black, content - 6, content))
    owner = np.where(owner == 1, 2, np.where(owner == 2, 1, 0))
    r["squares"] = (content | terrain | (owner << 6)).astype(np.uint8)
    r["gold"] = r["gold"][:, ::-1]
    r["side"] = 1 - r["side"]
    return r


def per_position(idx, off, b):
    ends = np.r_[off[1:], len(idx)]
    return [np.sort(idx[off[i]:ends[i]]) for i in range(b)]


def main(path):
    rec = np.array(GGData(path).records[:500])
    b = len(rec)
    n = int(round(rec["squares"].shape[1] ** 0.5))
    nn = n * n
    base = encode(rec)
    us0 = per_position(base[0], base[1], b)
    them0 = per_position(base[2], base[3], b)

    # Expected count: one feature per piece/treasure plus one per non-open terrain square
    content = rec["squares"] & 15
    terrain = (rec["squares"] >> 4) & 3
    expected = (content > 0).sum(1) + (terrain > 0).sum(1)
    assert all(len(u) == e for u, e in zip(us0, expected)), "feature count"
    assert all(u.max() < NUM_BLOCKS * nn for u in us0 if len(u))

    # Colour swap: us/them features and dense inputs must be identical
    sw = encode(swap_colors(rec))
    assert all((a == c).all() for a, c in zip(us0, per_position(sw[0], sw[1], b))), "colour swap (us)"
    assert all((a == c).all() for a, c in zip(them0, per_position(sw[2], sw[3], b))), "colour swap (them)"
    assert np.allclose(base[4], sw[4]), "colour swap (dense)"

    # Symmetries: same multiset of blocks, squares moved by the permutation
    perms = symmetry_perms(n)
    for s in range(8):
        inv = np.argsort(perms[s])          # old square -> new square
        enc = encode(rec, s)
        us = per_position(enc[0], enc[1], b)
        for a, c in zip(us0, us):
            moved = np.sort((a // nn) * nn + inv[a % nn])
            assert (moved == c).all(), f"symmetry {s}"
        assert np.allclose(base[4], enc[4]), f"symmetry {s} (dense)"
    print(f"features OK on {b} positions: counts, colour swap, 8 symmetries "
          f"(avg {np.mean([len(u) for u in us0]):.1f} active features per perspective)")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "training/data/smoke.bin")
