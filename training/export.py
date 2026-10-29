"""Export a trained model for the Rust engine (engine/src/nnue.rs).

    python training/export.py --run m10_1m [--check-data training/data/holdout_20k.bin]

Writes training/runs/<run>/net.ggnn and, with --check-data, check.bin: positions with the
logits PyTorch computes for them, so `gg nncheck` can confirm the engine gets the same numbers.
"""
import argparse
import os
import struct

import numpy as np
import torch

from features import encode
from ggdata import GGData
from model import EvalNet

NET_MAGIC = b"GGNN"
NET_VERSION = {4: 1, 8: 2}   # by number of dense inputs (v2 = gold-race inputs); 3 = v2 + race term
CHECK_MAGIC = b"GGNC"


def f32(t):
    return t.detach().cpu().numpy().astype("<f4").tobytes()


def main():
    p = argparse.ArgumentParser(description="Export a trained model for the engine")
    p.add_argument("--run", required=True)
    p.add_argument("--check-data", help="gendata file to take check positions from")
    p.add_argument("--check-count", type=int, default=2000)
    args = p.parse_args()

    run_dir = os.path.join("training", "runs", args.run)
    ckpt = torch.load(os.path.join(run_dir, "best.pt"), weights_only=False)
    model = EvalNet(**ckpt["config"])
    model.load_state_dict(ckpt["state"])
    model.eval()
    c = ckpt["config"]
    size = ckpt["board_size"]

    path = os.path.join(run_dir, "net.ggnn")
    with open(path, "wb") as f:
        f.write(NET_MAGIC)
        version = 3 if model.race else NET_VERSION[model.num_dense]
        f.write(struct.pack("<6I", version, size, c["num_features"], c["hidden"], c["l2"], c["l3"]))
        f.write(struct.pack("<f", ckpt["k"]))
        if version == 3:
            f.write(struct.pack("<f", model.race))   # race term, logit units
        f.write(f32(model.ft.weight))      # [feature][hidden]
        f.write(f32(model.ft_bias))
        f.write(f32(model.l1.weight))      # [out][in]
        f.write(f32(model.l1.bias))
        f.write(f32(model.l2.weight))
        f.write(f32(model.l2.bias))
        f.write(f32(model.out.weight))
        f.write(f32(model.out.bias))
    print(f"Wrote {path} ({os.path.getsize(path) / 1e6:.1f} MB, K = {ckpt['k']:.0f})")

    if args.check_data:
        d = GGData(args.check_data)
        if d.board_size != size:
            raise SystemExit(f"check data is {d.board_size}x{d.board_size}, model is {size}x{size}")
        rng = np.random.default_rng(0)
        idx = np.sort(rng.choice(len(d), min(args.check_count, len(d)), replace=False))
        rec = np.array(d.records[idx])
        with torch.no_grad():
            logits = model(*[torch.from_numpy(x) for x in encode(rec, 0, model.num_dense)]).numpy().astype("<f4")
        cpath = os.path.join(run_dir, "check.bin")
        with open(cpath, "wb") as f:
            f.write(CHECK_MAGIC)
            f.write(struct.pack("<3I", len(rec), rec.dtype.itemsize, size))
            for r, lg in zip(rec, logits):
                f.write(r.tobytes())
                f.write(struct.pack("<f", lg))
        print(f"Wrote {cpath} ({len(rec)} positions with PyTorch logits)")


if __name__ == "__main__":
    main()
