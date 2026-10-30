# ./engine/target/release/gg.exe gendata --seeds engine/testdata/seeds_m10_200k.txt --games 9500 --nodes 20000 --threads 10 --out training/data/m10_1m.bin
# python training/ggdata.py training/data/m10_1m.bin
# python training/train.py --data training/data/m10_1m.bin --run m10_1m

"""Train the evaluation network on `gg gendata` files. See docs/training.md.

    python training/train.py --data "training/data/*.bin" --run first
"""
import argparse
import glob
import json
import math
import os
import queue
import threading
import time
import zlib

import numpy as np
import torch

from features import encode, num_features
from ggdata import FLAG_BEST_TACTICAL, FLAG_IN_CHECK, GGData
from model import ML_SCALE, EvalNet


# ── Logging helpers (conventions: docs/training.md) ──────────────────────────────────────────

def ticker(stop_event):
    """Liveness ticker: '-' every minute, the minute number every 5 minutes ("  0----5----10").
    Prints nothing for steps shorter than a minute."""
    start = time.time()
    last = 0
    while not stop_event.wait(1.0):
        minute = int((time.time() - start) / 60)
        if minute > last:
            if last == 0:
                print("  0", end="", flush=True)
            last = minute
            print(f"{minute}" if minute % 5 == 0 else "-", end="", flush=True)
    if last:
        print()


class Ticker:
    def __enter__(self):
        self.stop = threading.Event()
        self.thread = threading.Thread(target=ticker, args=(self.stop,), daemon=True)
        self.thread.start()
        return self

    def __exit__(self, *exc):
        self.stop.set()
        self.thread.join()


# ── Data ─────────────────────────────────────────────────────────────────────────────────────

def plies_left(records):
    """Plies from each position to the end of its game (1 = the game ends with this move);
    -1 for games that didn't finish (ply cap). All records of a game must be in `records`."""
    games = records["game"]
    ply = records["ply"].astype(np.int32)
    _, inv = np.unique(games, return_inverse=True)
    last = np.zeros(inv.max() + 1 if len(inv) else 0, dtype=np.int32)
    np.maximum.at(last, inv, ply)
    left = last[inv] - ply + 1
    left[records["reason"] == 0] = -1
    return left.astype(np.int16)


def load(paths, args):
    """Select usable positions; split train/val by game (never the same game in both)."""
    files, train, val = [], [], []
    stats = dict(positions=0, in_check=0, tactical=0, games=0)
    for fi, path in enumerate(paths):
        d = GGData(path)
        files.append(d)
        r = d.records
        d.left = plies_left(r)
        stats["positions"] += len(r)
        flags = r["flags"]
        keep = np.ones(len(r), dtype=bool)
        if args.skip_check:
            stats["in_check"] += int((flags & FLAG_IN_CHECK > 0).sum())
            keep &= (flags & FLAG_IN_CHECK) == 0
        if args.skip_tactical:
            stats["tactical"] += int((flags & FLAG_BEST_TACTICAL > 0).sum())
            keep &= (flags & FLAG_BEST_TACTICAL) == 0
        games = r["game"].astype(np.uint64)
        stats["games"] += len(np.unique(games))
        # Stable hash of (file name, game) -> validation bucket. Keyed by the name, not the file's
        # position in the sorted list: adding a file (nn10_ sorts before nn1_) must not move
        # games of other files between train and val, or a run continued from an earlier
        # network (--init) validates on games that network trained on. (Fixed 2026-10-30.)
        key = np.uint64(zlib.crc32(os.path.basename(path).encode()))
        h = ((games + key * np.uint64(1 << 32)) * np.uint64(2654435761)) % np.uint64(1000)
        is_val = h < np.uint64(int(1000 * args.val_fraction))
        idx = np.nonzero(keep)[0]
        train.append(np.stack([np.full(len(idx), fi), idx], axis=1)[~is_val[idx]])
        val.append(np.stack([np.full(len(idx), fi), idx], axis=1)[is_val[idx]])
    train = np.concatenate(train)
    val = np.concatenate(val)
    if args.max_positions and len(train) > args.max_positions:
        train = train[np.random.default_rng(0).choice(len(train), args.max_positions, replace=False)]
    return files, train, val, stats


def gather(files, rows, field="records"):
    """Records (or another per-record array of the files, e.g. "left") for (file, index) rows,
    grouped by file and sorted by index within a file (the same order for every field)."""
    if len(files) == 1:
        return getattr(files[0], field)[np.sort(rows[:, 1])]
    parts = [getattr(files[fi], field)[np.sort(rows[rows[:, 0] == fi, 1])] for fi in np.unique(rows[:, 0])]
    return np.concatenate(parts)


def targets(records, k, lam):
    """λ · sigmoid(search score / K) + (1 − λ) · game result, from the side to move's view."""
    score = records["score"].astype(np.float32)
    result = (records["result"].astype(np.float32) + 1.0) / 2.0
    return lam / (1.0 + np.exp(np.clip(-score / k, -50.0, 50.0))) + (1.0 - lam) * result


def batches(files, rows, batch_size, k, lam, augment, rng, num_dense):
    """Background thread: encode batch i+1 while the model trains on batch i."""
    q = queue.Queue(maxsize=4)

    def work():
        for s in range(0, len(rows), batch_size):
            rec = gather(files, rows[s:s + batch_size])
            sym = int(rng.integers(8)) if augment else 0
            feats = encode(rec, sym, num_dense)
            left = gather(files, rows[s:s + batch_size], "left")
            q.put((feats, targets(rec, k, lam), rec["result"].astype(np.int8), left))
        q.put(None)

    threading.Thread(target=work, daemon=True).start()
    while (item := q.get()) is not None:
        yield item


def to_torch(feats):
    return [torch.from_numpy(f) for f in feats]


def fit_k(files, rows, rng, sample=200_000):
    """Scale K for score -> win probability: minimises log loss of sigmoid(score/K) vs. the result."""
    rec = gather(files, rows[rng.choice(len(rows), min(sample, len(rows)), replace=False)])
    decisive = (rec["result"] != 0) & (np.abs(rec["score"]) < 32000)
    s = rec["score"][decisive].astype(np.float64)
    y = (rec["result"][decisive] > 0).astype(np.float64)
    best = None
    for k in np.geomspace(20, 5000, 200):
        p = np.clip(1 / (1 + np.exp(-s / k)), 1e-6, 1 - 1e-6)
        ll = -(y * np.log(p) + (1 - y) * np.log(1 - p)).mean()
        if best is None or ll < best[1]:
            best = (k, ll)
    return float(best[0])


def winner_accuracy(pred, result, mask=None):
    """Share of decisive positions where the sign of `pred` names the eventual winner."""
    decisive = result != 0
    if mask is not None:
        decisive &= mask
    return float(((pred[decisive] > 0) == (result[decisive] > 0)).mean()) if decisive.any() else 0.0


# "Close" positions: the search score gives the side to move a 30-70% chance, |score| <= 0.85 K.
CLOSE_LOGIT = math.log(0.7 / 0.3)


def search_gap(logit, search_logit, mask=None):
    """Mean |win chance(eval) - win chance(search score)|: how well an eval anticipates the search."""
    sig = lambda x: 1.0 / (1.0 + np.exp(-np.clip(x, -50, 50)))
    d = np.abs(sig(logit) - sig(search_logit))
    return float(d[mask].mean() if mask is not None else d.mean())


# ── Training ─────────────────────────────────────────────────────────────────────────────────

def ml_loss(ml, left):
    """Moves-left loss over finished games: (MSE in units of ML_SCALE plies, sum of abs errors in plies, count)."""
    left = torch.from_numpy(left.astype(np.float32))
    mask = left >= 0
    if not mask.any():
        return torch.zeros(()), 0.0, 0
    d = ml[mask] - left[mask] / ML_SCALE
    return torch.mean(d ** 2), float(d.detach().abs().sum()) * ML_SCALE, int(mask.sum())


def evaluate(model, files, rows, args, k):
    """(value loss, moves-left loss, moves-left mean abs error in plies, logits, results)."""
    model.eval()
    total, n, preds, results = 0.0, 0, [], []
    ml_total, ml_abs, ml_n = 0.0, 0.0, 0
    with torch.no_grad():
        for feats, tgt, res, left in batches(files, rows, args.batch_size, k, args.lam, False, None, args.dense):
            out, ml = model(*to_torch(feats), with_ml=True)
            total += torch.mean((torch.sigmoid(out) - torch.from_numpy(tgt)) ** 2).item() * len(tgt)
            n += len(tgt)
            if ml is not None:
                l, a, c = ml_loss(ml, left)
                ml_total += l.item() * c
                ml_abs += a
                ml_n += c
            preds.append(out.numpy())
            results.append(res)
    return (total / max(n, 1), ml_total / max(ml_n, 1), ml_abs / max(ml_n, 1),
            np.concatenate(preds), np.concatenate(results))


def main():
    p = argparse.ArgumentParser(description="Train the evaluation network")
    p.add_argument("--data", default="training/data/*.bin", help="glob of gendata files")
    p.add_argument("--run", default=time.strftime("run_%Y%m%d_%H%M"), help="name under training/runs/")
    p.add_argument("--epochs", type=int, default=100, help="upper limit; early stopping usually ends sooner")
    p.add_argument("--batch-size", type=int, default=8192)
    p.add_argument("--lr", type=float, default=1e-3)
    p.add_argument("--lr-factor", type=float, default=0.5, help="multiply lr by this when val loss stalls")
    p.add_argument("--lr-patience", type=int, default=1, help="epochs without val improvement before lowering lr")
    p.add_argument("--min-lr", type=float, default=1e-5)
    p.add_argument("--lam", type=float, default=0.75, help="weight of the search score vs. the game result")
    p.add_argument("--hidden", type=int, default=256)
    p.add_argument("--dense", type=int, default=8, choices=[4, 8],
                   help="dense inputs: 4 = v1 (gold, mines), 8 = v2 (+ gold race)")
    p.add_argument("--race-residual", type=float, default=0.0,
                   help="add the hand-written eval's gold-race term with this weight (eval units, its "
                        "default is 2000) to the output; the network learns the rest. 0 = off")
    p.add_argument("--moves-left", action="store_true", help="add the moves-left head (plies until the game ends)")
    p.add_argument("--ml-loss-weight", type=float, default=0.05, help="weight of the moves-left loss")
    p.add_argument("--init", help="start from this run's best.pt (same layers; keeps its K)")
    p.add_argument("--freeze-trunk", action="store_true",
                   help="with --init: train only the moves-left head; the value output stays exactly the same")
    p.add_argument("--patience", type=int, default=4, help="early stopping: epochs without val improvement")
    p.add_argument("--val-fraction", type=float, default=0.05)
    p.add_argument("--max-positions", type=int, default=0, help="subsample training positions (0 = all)")
    p.add_argument("--no-augment", dest="augment", action="store_false", help="disable the 8 board symmetries")
    p.add_argument("--keep-check", dest="skip_check", action="store_false", help="keep positions in check")
    p.add_argument("--keep-tactical", dest="skip_tactical", action="store_false",
                   help="keep positions whose best move is a capture/treasure/mine grab")
    p.add_argument("--threads", type=int, default=os.cpu_count())
    p.add_argument("--seed", type=int, default=1)
    args = p.parse_args()

    torch.set_num_threads(args.threads)
    torch.manual_seed(args.seed)
    rng = np.random.default_rng(args.seed)
    run_dir = os.path.join("training", "runs", args.run)
    os.makedirs(run_dir, exist_ok=True)
    t_start = time.time()

    print(f"Using cpu ({args.threads} threads)")
    paths = sorted(glob.glob(args.data))
    if not paths:
        raise SystemExit(f"no files match {args.data}")
    files, train, val, st = load(paths, args)
    sizes = {f.board_size for f in files}
    if len(sizes) != 1:
        raise SystemExit(f"mixed board sizes {sizes}; train one size at a time")
    size = sizes.pop()
    print(f"Data: {len(paths)} file(s), {st['positions']:,} positions from {st['games']:,} games, board {size}x{size}")
    print(f"  skipped: {st['in_check']:,} in check, {st['tactical']:,} with a tactical best move (overlap counted twice)")
    print(f"  train {len(train):,} / val {len(val):,} positions (split by game, {100 * args.val_fraction:.0f}% val)")

    init = None
    if args.init:
        init = torch.load(os.path.join("training", "runs", args.init, "best.pt"), weights_only=False)
        k = init["k"]
    else:
        k = fit_k(files, train, rng)
    val_rec = gather(files, val)
    val_result = val_rec["result"]
    search = val_rec["score"].astype(np.float32)
    close = np.abs(search) <= CLOSE_LOGIT * k
    base = dict(search=(winner_accuracy(search, val_result), winner_accuracy(search, val_result, close)))
    if all(f.has_static_eval for f in files):
        static = val_rec["static_eval"].astype(np.float32)
        base["static"] = (winner_accuracy(static, val_result), winner_accuracy(static, val_result, close))
        base["static_gap"] = (search_gap(static / k, search / k), search_gap(static / k, search / k, close))
    print(f"Score scale K = {k:.0f} eval units ({k / 10:.1f} gold per logit unit)")
    print(f"Close positions (search score: 30-70% win chance): {100 * close.mean():.1f}% of val")
    print("Baselines, winner predicted (all / close val positions):")
    print(f"  engine search score:      {100 * base['search'][0]:.1f}% / {100 * base['search'][1]:.1f}%")
    if "static" in base:
        print(f"  hand-written static eval: {100 * base['static'][0]:.1f}% / {100 * base['static'][1]:.1f}%")
        print(f"Static eval vs search score, avg gap in win chance (all / close): "
              f"{100 * base['static_gap'][0]:.1f} / {100 * base['static_gap'][1]:.1f} points")
    else:
        print("  hand-written static eval: n/a (data files from before format version 2)")

    race = args.race_residual / k if args.race_residual else 0.0
    if race and args.dense < 8:
        raise SystemExit("--race-residual needs --dense 8")
    if init:
        cfg = dict(init["config"])
        cfg["moves_left"] = cfg.get("moves_left", False) or args.moves_left
        if cfg.get("num_dense", 4) != args.dense:
            raise SystemExit(f"--dense {args.dense}, but {args.init} has {cfg.get('num_dense', 4)} dense inputs")
        model = EvalNet(**cfg)
        missing, _ = model.load_state_dict(init["state"], strict=False)
        print(f"Initialised from {args.init} (K {k:.0f}); new: {', '.join(missing) or 'nothing'}")
    else:
        model = EvalNet(num_features(size), hidden=args.hidden, num_dense=args.dense, race=race,
                        moves_left=args.moves_left)
    if args.freeze_trunk:
        if not (init and model.moves_left):
            raise SystemExit("--freeze-trunk needs --init and --moves-left")
        for p_ in model.trunk_parameters():
            p_.requires_grad_(False)
        print("Trunk frozen: training the moves-left head only")
    if model.moves_left:
        vl = gather(files, val, "left")
        vl = vl[vl >= 0].astype(np.float64)
        base["ml_median_mae"] = float(np.abs(vl - np.median(vl)).mean())
        print(f"Moves-left head: loss weight {args.ml_loss_weight} (MSE in units of {ML_SCALE:.0f} plies); "
              f"baseline (always the median, {np.median(vl):.0f}): {base['ml_median_mae']:.1f} plies mean abs error")
    if race:
        print(f"Race residual: fixed gold-race term {args.race_residual:g} eval units "
              f"({race:.2f} logit) x (1/(t_us+1) - 1/(t_them+1)) added to the network")
    print(f"Model parameters: {model.param_count():,}")
    trainable = [p_ for p_ in model.parameters() if p_.requires_grad]
    opt = torch.optim.Adam(trainable, lr=args.lr)
    # Lower the learning rate when validation loss stalls (instead of a fixed per-epoch decay)
    sched = torch.optim.lr_scheduler.ReduceLROnPlateau(opt, factor=args.lr_factor, patience=args.lr_patience,
                                                       min_lr=args.min_lr)

    history = []
    best = dict(val_loss=math.inf, epoch=0)
    bad_epochs = 0
    for epoch in range(1, args.epochs + 1):
        t0 = time.time()
        model.train()
        order = train[rng.permutation(len(train))]
        total, n, preds, results = 0.0, 0, [], []
        nan_batches = clipped = 0
        with Ticker():
            for feats, tgt, res, left in batches(files, order, args.batch_size, k, args.lam, args.augment, rng, args.dense):
                out, ml = model(*to_torch(feats), with_ml=True)
                vloss = torch.mean((torch.sigmoid(out) - torch.from_numpy(tgt)) ** 2)
                loss = vloss + args.ml_loss_weight * ml_loss(ml, left)[0] if ml is not None else vloss
                if not torch.isfinite(loss):
                    nan_batches += 1
                    continue
                opt.zero_grad()
                loss.backward()
                if torch.nn.utils.clip_grad_norm_(trainable, max_norm=10.0) > 10.0:
                    clipped += 1
                opt.step()
                total += vloss.item() * len(tgt)
                n += len(tgt)
                preds.append(out.detach().numpy())
                results.append(res)
        train_loss = total / max(n, 1)
        train_acc = winner_accuracy(np.concatenate(preds), np.concatenate(results))
        val_vloss, val_mlloss, val_ml_mae, val_pred, val_res = evaluate(model, files, val, args, k)
        # Selection / early stopping on the total loss (value + weighted moves-left)
        val_loss = val_vloss + (args.ml_loss_weight * val_mlloss if model.moves_left else 0.0)
        val_acc = winner_accuracy(val_pred, val_res)
        val_close = winner_accuracy(val_pred, val_res, close)
        val_gap = search_gap(val_pred, search / k)
        val_gap_close = search_gap(val_pred, search / k, close)
        elapsed = time.time() - t0
        if clipped:
            print(f"    Clipped large gradients in {clipped} batches")
        if nan_batches:
            print(f"    Skipped {nan_batches} batches due to NaN/inf loss")
        ml_txt = f"  moves-left err={val_ml_mae:.1f} plies" if model.moves_left else ""
        print(f"Epoch {epoch:3d}/{args.epochs} | loss={train_loss:.5f}/{val_vloss:.5f}  "
              f"acc={100 * train_acc:.1f}%/{100 * val_acc:.1f}%  close={100 * val_close:.1f}%  "
              f"gap={100 * val_gap:.1f}/{100 * val_gap_close:.1f}{ml_txt}  |  {elapsed:.1f}s")
        lr = opt.param_groups[0]["lr"]
        history.append(dict(epoch=epoch, train_loss=train_loss, val_loss=val_vloss, val_ml_mae=val_ml_mae,
                            train_acc=train_acc, val_acc=val_acc, val_close_acc=val_close,
                            val_gap=val_gap, val_gap_close=val_gap_close, lr=lr, time_s=elapsed))
        sched.step(val_loss)
        if opt.param_groups[0]["lr"] < lr:
            print(f"  Learning rate {lr:.2e} -> {opt.param_groups[0]['lr']:.2e}")

        if val_loss < best["val_loss"]:
            best = dict(val_loss=val_loss, val_value_loss=val_vloss, val_acc=val_acc, val_close_acc=val_close,
                        val_gap=val_gap, val_gap_close=val_gap_close, epoch=epoch)
            if model.moves_left:
                best["val_ml_mae_plies"] = val_ml_mae
            bad_epochs = 0
            torch.save(dict(state=model.state_dict(), config=model.config, k=k, board_size=size,
                            args=vars(args)), os.path.join(run_dir, "best.pt"))
            print(f"  Saved best model (val_loss={val_loss:.5f})")
        else:
            bad_epochs += 1
            if bad_epochs >= args.patience:
                print(f"  Early stopping after {epoch} epochs (patience={args.patience})")
                break

    total_min = (time.time() - t_start) / 60
    line = "=" * 44
    print(f"\n{line}\n  Run {args.run} - spreadsheet summary\n{line}")
    print(f"  Val loss:              {best['val_value_loss']:.5f}")
    if model.moves_left:
        print(f"  Moves-left error:      {best['val_ml_mae_plies']:.1f} plies (mean abs)")
    print(f"  Val accuracy:          {100 * best.get('val_acc', 0):.1f}%")
    print(f"  Val accuracy (close):  {100 * best.get('val_close_acc', 0):.1f}%")
    print(f"  Search score accuracy: {100 * base['search'][0]:.1f}% / {100 * base['search'][1]:.1f}% close")
    if "static" in base:
        print(f"  Static eval accuracy:  {100 * base['static'][0]:.1f}% / {100 * base['static'][1]:.1f}% close")
    print(f"  Gap to search (net):   {100 * best.get('val_gap', 0):.1f} / {100 * best.get('val_gap_close', 0):.1f} close")
    if "static" in base:
        print(f"  Gap to search (static):{100 * base['static_gap'][0]:5.1f} / {100 * base['static_gap'][1]:.1f} close")
    print(f"  Close positions:       {100 * close.mean():.1f}% of val")
    print(f"  Best epoch:            {best['epoch']}")
    print(f"  Train positions:       {len(train):,}")
    print(f"  Games:                 {st['games']:,}")
    print(f"  Parameters:            {model.param_count():,}")
    print(f"  K (eval units):        {k:.0f}")
    print(f"  Time (min):            {total_min:.1f}")
    print(line)

    print(f"\n{'Epoch':>5}  {'Train loss':>10}  {'Val loss':>9}  {'Train acc':>9}  {'Val acc':>8}  {'Close':>6}  {'Time':>7}")
    print(f"{'-' * 5}  {'-' * 10}  {'-' * 9}  {'-' * 9}  {'-' * 8}  {'-' * 6}  {'-' * 7}")
    for h in history:
        print(f"{h['epoch']:5d}  {h['train_loss']:10.5f}  {h['val_loss']:9.5f}  {100 * h['train_acc']:8.1f}%  "
              f"{100 * h['val_acc']:7.1f}%  {100 * h['val_close_acc']:5.1f}%  {h['time_s'] / 60:6.1f}m")

    with open(os.path.join(run_dir, "summary.json"), "w") as f:
        json.dump(dict(args=vars(args), k=k, baselines=base, close_fraction=float(close.mean()), best=best, history=history,
                       data=dict(files=paths, **st, train=len(train), val=len(val))), f, indent=2)
    print(f"\nSaved {run_dir}/best.pt and summary.json")


if __name__ == "__main__":
    main()
