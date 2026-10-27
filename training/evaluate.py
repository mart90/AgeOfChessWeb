"""Score a trained model against the engine's search and the hand-written static eval.

    python training/evaluate.py --run m10_1m --data training/data/fresh.bin

Use data the model wasn't trained on (fresh games). Positions in check or with a tactical best
move are skipped, as in training. Metrics: see docs/training.md.
"""
import argparse
import glob
import os

import numpy as np
import torch

from features import encode
from ggdata import FLAG_BEST_TACTICAL, FLAG_IN_CHECK, GGData
from model import EvalNet
from train import CLOSE_LOGIT, search_gap, winner_accuracy


def main():
    p = argparse.ArgumentParser(description="Evaluate a trained model on gendata files")
    p.add_argument("--run", required=True, help="run name under training/runs/")
    p.add_argument("--data", required=True, help="glob of gendata files (unseen games)")
    p.add_argument("--batch-size", type=int, default=8192)
    args = p.parse_args()

    ckpt = torch.load(os.path.join("training", "runs", args.run, "best.pt"), weights_only=False)
    model = EvalNet(**ckpt["config"])
    model.load_state_dict(ckpt["state"])
    model.eval()
    k = ckpt["k"]

    recs = []
    for path in sorted(glob.glob(args.data)):
        d = GGData(path)
        r = np.array(d.records)
        keep = (r["flags"] & (FLAG_IN_CHECK | FLAG_BEST_TACTICAL)) == 0
        recs.append((r[keep], d.has_static_eval))
    has_static = all(h for _, h in recs)
    rec = np.concatenate([r for r, _ in recs])

    preds = []
    with torch.no_grad():
        for s in range(0, len(rec), args.batch_size):
            feats = encode(rec[s:s + args.batch_size])
            preds.append(model(*[torch.from_numpy(f) for f in feats]).numpy())
    net = np.concatenate(preds)                       # logits
    search = rec["score"].astype(np.float32) / k      # logits
    result = rec["result"]
    close = np.abs(search) <= CLOSE_LOGIT

    print(f"Model {args.run} (K = {k:.0f}) on {len(rec):,} quiet positions from {len(np.unique(rec['game'])):,} games "
          f"({100 * close.mean():.1f}% close)")
    print(f"{'':26}{'winner all':>11}{'close':>8}{'gap all':>9}{'close':>8}")
    rows = [("engine search score", search, None)]
    if has_static:
        rows.append(("hand-written static eval", rec["static_eval"].astype(np.float32) / k, True))
    rows.append((f"network ({args.run})", net, True))
    for name, x, gap in rows:
        g = f"{100 * search_gap(x, search):8.1f}{100 * search_gap(x, search, close):8.1f}" if gap else f"{'-':>8}{'-':>8}"
        print(f"  {name:24}{100 * winner_accuracy(x, result):10.1f}%{100 * winner_accuracy(x, result, close):7.1f}%{g}")
    print("  (gap = avg difference in win chance vs. the search score, percentage points; lower is better)")


if __name__ == "__main__":
    main()
