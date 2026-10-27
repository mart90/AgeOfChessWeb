# Evaluation-network training

_Set up 2026-10-26. Replaces the old `policyNetwork/` experiments (removed; history in git)._

## Approach

A small NNUE-style **value network** becomes the engine's evaluation function: the alpha-beta
search stays, the network replaces the hand-written eval at the leaves (and in pruning
decisions). Reasons: search scales steeply (5× nodes ≈ +440 Elo) and eval changes alone gave
+200 Elo, so evaluation looks like the bottleneck; the bot runs in the browser, which rules out
big networks; and labels come cheaply from the engine on CPUs.

Loop: engine-vs-engine games → labelled positions → train → put the net in the engine → test
with `gg match` **at equal time** → the stronger engine generates the next data. Start with
10–20M positions, then decide from learning curves (1M / 3M / 10M) whether more data pays off.

## Layout

| Path | What |
| --- | --- |
| `engine/src/traindata.rs` | Record format (writer). **Single definition; keep `training/ggdata.py` in sync.** |
| `engine/src/bin/gg.rs` → `gg gendata` | Plays engine-vs-engine games and writes records |
| `training/ggdata.py` | Reader (numpy memmap) + `python training/ggdata.py FILE` summary/sanity check |
| `training/features.py` | Network inputs (feature set v1) incl. the 8 board symmetries |
| `training/test_features.py` | Checks: feature counts, colour swap, symmetries |
| `training/model.py` | `EvalNet` v1 |
| `training/train.py` | Training with the logging conventions below |
| `training/evaluate.py` | Scores a trained model vs. the search and the static eval on unseen games |
| `training/export.py` | Writes `runs/<run>/net.ggnn` for the engine (+ `check.bin` with PyTorch outputs) |
| `engine/src/nnue.rs` | The network in the engine: loading, features, incremental evaluation |
| `training/data/`, `training/runs/` | Generated data and run outputs (gitignored) |

Python 3.11 + PyTorch (CPU is enough for v1) + numpy: `pip install -r training/requirements.txt`.

## Commands

```
# Boards (server map generator; 200k mirrored 10x10 in ~5 s)
dotnet tools/RulesOracle/bin/Release/net9.0/RulesOracle.dll seeds 200000 10 m engine/testdata/seeds_m10_200k.txt

# Data: every position with its search score, plus the game result
cd engine && ./target/release/gg gendata --seeds testdata/seeds_m10_200k.txt --games 2000 \
    --threads 10 --nodes 10000 --out ../training/data/dev_2k.bin
#   --random-plies K (default 8): each game plays its first 0..K plies at random among the top
#   --random-multipv (4) lines within --random-margin (3) gold; --first-game-id to keep ids
#   unique across files; --seed-offset N to start at line N of the seed file (new boards for each
#   run); --overwrite to replace an existing file. Generation is deterministic: the same seeds +
#   options give the same games. Keep the last 20k seeds (seeds_m10_holdout.txt) for evaluation.

python training/ggdata.py training/data/dev_2k.bin       # summary + sanity checks
python training/test_features.py training/data/dev_2k.bin
python training/train.py --data "training/data/*.bin" --run NAME
```

## Data format (v1)

32-byte header (`GGTD`, version, board size N, record size, rules, nodes per move), then one
fixed-size record per position (N·N + 24 bytes; 124 bytes on 10×10): one byte per square
(content, terrain, mine owner), side to move, flags (in check, best move tactical, random
opening move, mate score), gold, ply, search score for the side to move (1 gold = 10), game
result for the side to move, end reason, game id, best move, depth, and (version 2+) the
hand-written eval's static score. Version 1 files are still readable. Details in
`engine/src/traindata.rs`.

## Network v1

- **Inputs** (`features.py`): per perspective (side to move, opponent) 18 blocks × N·N one-hot
  features — own/enemy K Q R B N P, treasure, rocks, forest, mine unowned / own / enemy —
  about 26 active per perspective. Dense: gold / gold threshold and owned mines / 10, both sides.
- **Model** (`model.py`): shared feature transformer 1800 → 256 per perspective (clipped ReLU),
  concat + dense → 32 → 32 → 1 (win-probability logit for the side to move). 479k parameters.
- **Labels**: `λ · sigmoid(score / K) + (1 − λ) · result` with λ = 0.75; MSE on probabilities.
  K is fitted on the data (log loss of score vs. result); engine eval = logit × K.
- **Filters**: positions in check and positions whose best move is tactical are skipped (the
  eval is only used in quiet positions). Validation split by game (5%).
- **Schedule**: Adam, lr 1e-3, halved whenever val loss doesn't improve for an epoch
  (`ReduceLROnPlateau`); early stopping after 4 epochs without improvement; at most 100 epochs.
- **Metrics** (per epoch and in the summary, on val positions):
  - *winner accuracy* — sign of the eval names the eventual winner; also for *close* positions
    (search score says 30–70%). Coarse: the hand-written static eval already gets ~78% overall,
    and close positions are near coin flips for every eval.
  - *gap to search* — mean |win chance(eval) − win chance(search score)| in percentage points,
    all / close. The sharpest offline measure of eval quality; compare the network with the
    hand-written static eval (format v2 data stores it). First bar: the static eval's ~5 points.
- **Augmentation**: the rules don't depend on orientation (pawns move/capture in all four
  directions), so each batch uses one of the 8 rotations/reflections at random.

## Logging conventions (from the old `policyNetwork/` code; keep using them)

- Start: device line (`Using cpu`), parameter count, data summary (positions, games, split).
- One line per epoch: `Epoch   3/20 | loss=train/val  acc=train%/val%  |  12.3s`, then
  `Saved best model (val_loss=…)` or `Early stopping after N epochs (patience=P)`.
- Liveness ticker during long steps: `-` every minute, the minute number every 5 minutes on one
  line (`  0----5----10`); `gg gendata` adds progress at the 5-minute marks (`5[312 games]`).
- Warnings only when something happened: clipped gradients (count), skipped NaN/inf batches.
- End of run: an aligned **"spreadsheet summary"** block (`Label:   value` lines, one per metric,
  meant to be copied into a spreadsheet), then a summary **table** with one row per epoch /
  iteration and a total time line.
- Model comparison: Elo estimate from the score vs. the current best
  (`best + 400·log10(s/(1−s))`), running Elo kept in a file and seeded by hand; a new model
  is accepted at ≥ 50%; the previous best is backed up. Placement percentages per piece type
  (Q R B N P) and game-end statistics (avg moves, draws %, mates %) are part of every evaluation.
- Save one sample game per evaluation as JSON for inspection.
- Resumable runs (`--resume`); setup commands for a rented Linux box at the top of the script.

## First results (2026-10-26)

`dev_2k.bin`: 2,000 games at 10k nodes/move, 218k positions in 4.4 min (≈ 3M positions/hour
on 10 threads; ≈ 450k/hour at 60k nodes). Training on 117k filtered positions: 2.3 s/epoch on
CPU (≈ 50k positions/s, so ~7 min/epoch for 20M). After 6 epochs the net predicts the winner
of held-out positions 74.1% of the time; the 10k-node search score itself: 76.5%. K = 279.

## Network in the engine (2026-10-27)

```
python training/export.py --run m10_1m --check-data training/data/holdout_20k.bin
./engine/target/release/gg nncheck --net training/runs/m10_1m/net.ggnn --check training/runs/m10_1m/check.bin
./engine/target/release/gg match --a-nnue training/runs/m10_1m/net.ggnn --seeds engine/testdata/seeds_m10_holdout.txt --games 200 --nodes 12000
```

- `nnue.rs`: f32 inference. The first layer's sums are kept per search ply and updated lazily
  from the moves made (only when an evaluation is needed, so legality-test moves cost nothing);
  the next layer adds one transposed weight row per non-zero input. `Searcher.nnue` selects the
  network; `--nnue` / `--a-nnue` / `--b-nnue` in `gg`.
- Search path is quantised: first-layer sums int16 (scale 512), first dense layer int16 weights
  (scale 4096) with int32 sums, last two layers f32. Differs from PyTorch by 0.17 gold on average
  (max 1 gold); incremental vs. from scratch is exact (integer arithmetic).
- `gg nncheck`: float path vs. PyTorch on 2,000 positions — max logit difference 3e-6; quantised
  path as above; 40–60 searches with every evaluation re-checked — 0 mismatches.
- Search speed vs. the hand-written eval (2026-10-27, busy machine): f32 from scratch 19x
  slower → incremental 3.9x → int16 2.8x (generic x86-64 build) → **1.5–1.6x with
  `RUSTFLAGS="-C target-cpu=native"`** (AVX2). Use native builds for local/rented data
  generation and matches. The WASM build has no SIMD yet: add `-C target-feature=+simd128`
  (`engine/.cargo/config.toml`, wasm32 target) when the network goes to the browser
  (needs Safari 16.4+ on iOS; all other current browsers have it).
- Equal-nodes match, `m10_1m` network vs. hand-written eval, 12k nodes, 200 holdout boards
  played twice: **294–106 (73.5%), +177 ± 39 Elo** for the network (2026-10-27).

## Next steps

1. Equal-**time** match (that is what matters in the browser); then speed work if needed:
   integer quantisation + SIMD (also `wasm32` simd128), or a smaller first layer.
2. Learning curve: 3M positions (`m10_2m` + `m10_1m`); then larger datasets / deeper labels.
3. Browser: ship `net.ggnn` with the client, load it into the WASM engine, re-set the computer
   levels' node budgets.
