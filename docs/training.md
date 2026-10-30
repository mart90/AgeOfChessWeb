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
  about 26 active per perspective. Dense: gold / gold threshold and owned mines / 10, both sides
  (v1, 4 inputs). **v2 (2026-10-28, `train.py --dense 8`, the default; file version 2)** adds the
  gold race, both sides: turns to the threshold at the current income t (`eval::turns_to_gold`)
  as 1/(t+1) and min(t, 40)/40. Added after nn3 lost games on gold it thought it was winning.
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

## Self-play iterations (2026-10-27)

```
# generate with the current best network driving the search (static_eval in the records stays hand-written)
./engine/target/release/gg gendata --nnue training/runs/m10_3m/net.ggnn --seeds engine/testdata/seeds_m10_200k.txt     --seed-offset 28500 --first-game-id 28500 --games 14000 --nodes 12000 --threads 11 --out training/data/nn1_m10_1p5m.bin
python training/train.py --data "training/data/*m10_*.bin" --run m10_nn1      # from scratch, all data
python training/export.py --run m10_nn1 --check-data training/data/holdout_20k.bin
./engine/target/release/gg nncheck --net training/runs/m10_nn1/net.ggnn --check training/runs/m10_nn1/check.bin
# gate: new vs previous best on holdout boards
cd engine && ./target/release/gg match --a-nnue ../training/runs/m10_nn1/net.ggnn --b-nnue ../training/runs/m10_3m/net.ggnn     --seeds testdata/seeds_m10_holdout.txt --games 800 --threads 11 --nodes 12000
```

- Network-driven gendata: 1.43M positions/hour (12k nodes, 11 threads), more checkmates (55%).
- Use new boards each iteration (`--seed-offset` = previous end); keep clear of the holdout seeds.
- The holdout metrics (`evaluate.py`) barely move between networks (≈ 78% winner, close to the
  search's own 79%) — they are measured on hand-eval games. Gate on matches instead.

| Net | Data | vs previous | vs hand-written (12k nodes) |
|---|---|---|---|
| m10_1m | 1M hand-eval | — | +177 ± 39 |
| m10_3m | 3.2M hand-eval | +137 ± 26 vs 1m | +287 ± 33 |
| m10_nn1 | + 1.55M by 3m | +121 ± 18 vs 3m | +359 ± 27 |
| m10_nn2 | + 1.55M by nn1 (6.3M) | +50 ± 17 vs nn1 | +433 ± 32 |
| m10_nn2w | only the 3.1M network-played positions | −29 ± 17 vs nn2 | — |
| m10_nn3 | + 1.52M by nn2 (7.8M) | +60 ± 17 vs nn2 | +435 ± 32 |
| m10_nn3g500 | same as nn3, race residual 500 | −7 ± 17 vs nn3 | +461 ± 35 |
| m10_nn4 | + 1.51M by nn3g500 (9.3M), race residual 500 | +16 ± 17 vs nn3g500 | +466 ± 35 |
| m10_nn4w | only the 6.1M network-played positions (nn1–nn4 files), race 500 | −4 ± 17 vs nn4 | +420 ± 31 |
| m10_nn5 | window: nn2–nn5 files (6.1M), + 1.52M by nn4, race 500 | +7 ± 17 vs nn4 | +456 ± 34 |
| m10_nn6 | all 10.8M, + 1.52M by nn3, no race term, v1 inputs | +80 ± 17 vs nn3, +31 ± 17 vs nn5 | +521 ± 41 |
| m10_nn7 | all 12.3M, + 1.58M by nn6, no race term | +24 ± 17 vs nn6 | +486 ± 36 |
| m10_nn8 | all 13.8M, + 1.54M by nn7 | +35 ± 17 vs nn7, +128 ± 18 vs nn3 | — |
| m10_nn9 | all 15.4M, + 1.60M by nn8 | −2 ± 17 vs nn8, +113 ± 18 vs nn3 | — (not promoted) |
| **m10_nn10** (current) | all 17.0M, + 1.60M by nn8 | +26 ± 17 vs nn8, +158 ± 19 vs nn3 | — |

nn2's self-play ended in a gold victory only 9% of the time (3m: 42%, nn1: 35%). nn3 gains on
nn2 but not on the hand-written eval (92% score, where Elo estimates compress) — use a stronger
or more varied anchor (e.g. hand-written at more nodes, or older nets) before reading more into it.

Sliding window test (nn2w): dropping the 3.2M hand-eval positions loses, so at this size data
volume beats label quality; revisit once there are more network-played positions.

### Gold-race check (2026-10-28)

`gg match ... --out games.jsonl` saves every game (moves, the mover's search score per ply,
`a_is_white`). `engine/tools/find_gold_losses.py` lists games A lost on gold although its own
search had it clearly ahead shortly before; `tools/LibraryImport` puts them in a user's library.
nn3 at 12k nodes: lost 60 of 1600 games on gold vs hand-written (half of the hand-written eval's
119 wins) and 68 vs nn2; 13 of those after nn3 scored itself +15 gold or more in the last 40
plies, up to +116 gold. Top 5 are in Mart's library (dev DB) for review
(`engine/testdata/nn3_gold_losses.json`). Suggests a gold-race blind spot; Mart's review confirmed it (nn3 consolidates a material lead
while the opponent races to 200; doesn't give up material to stop the race). One game was
instead a missed depth-8 tactic (12k nodes ≈ depth 5).

**Race inputs (v2, `m10_nn3r`, same data and settings as nn3):** val loss 0.0175 vs 0.0177,
holdout 78.5% / close 57.5% vs 78.3% / 57.3%. Matches (12k nodes, 1600 games): **nn3r vs nn3
−2 ± 17**, vs hand-written +409 ± 30 (nn3 +435). Style changed, strength didn't: vs nn3 it wins
197 games on gold (nn3 107) but loses more by mate; vs hand-written it still loses 72 on gold
(nn3 60). The inputs alone don't fix it, which points at the labels: 75% of each target is the
search score of an engine with the same blind spot. Next: lower λ (more weight on results).

**Where the blind spot comes from (vs hand-written, 800 boards x 2, 12k nodes):** net's losses /
of which on gold / gold losses after scoring itself >= +15 gold: 3m 247 / 108 / 34, nn1 180 / 89 / 9,
nn2 121 / 59 / 7, nn3 119 / 60 / 10, nn3r 131 / 72 / 9. So it was worst in 3m (hand-eval labels
only) and self-play reduced it; not a self-play feedback loop. Gold stays ~half of the
hand-written eval's wins throughout. Likely cause: label horizon (~5-ply search vs a race decided
15+ turns later), whichever eval labels the data; only the result part (1 − λ) carries it.

**Race residual (v3, `train.py --race-residual 2000`, `m10_nn3g`):** the hand-written eval's race
term (weight 2000, = 7.6 logit at K 264) is a fixed part of the output; the network (v2 inputs)
learns the rest. Same data/settings as nn3. Val/holdout unchanged (78.3% / 57.1%). Matches:
**nn3g vs nn3 −27 ± 17** (nn3g wins 376 on gold but is mated 686 times; nn3 wins 88 on gold),
**vs hand-written +451 ± 34**, gold losses to it 44 (nn3 60) but nn3g itself wins 597 of its
1489 games on gold. So the fixed term turns it into a racer: fewer race losses, but nn3 punishes
the racing by mating it. Weight 2000 is probably too strong for a residual; try lower weights.
Mart's review of nn3g's top gold losses (in his library): no blind spot there; it was trying to
stop the race but missed mating attacks a deeper search would have found.

Weight sweep (same data/settings; 12k nodes, 800 holdout boards × 2; `games_nn3gW_vs_*_12k.jsonl`):

| weight | vs nn3 | its gold wins vs nn3 | vs hand | lost to hand on gold (≥ +15 conf.) | its gold wins vs hand |
|---|---|---|---|---|---|
| 0 (nn3) | — | — | +435 ± 32 | 60 (10) | 79 |
| 500 | −7 ± 17 | 227 | +461 ± 35 | 50 (6) | 272 |
| 1000 | −6 ± 17 | 520 | +446 ± 33 | 52 (6) | 1103 |
| 2000 | −27 ± 17 | 376 | +451 ± 34 | 44 (11) | 597 |

Weights 500/1000 are level with nn3 head-to-head and slightly stronger vs hand (within error),
with a few fewer gold losses. The weight mostly sets how much it races (1000 wins 69% of its games
vs hand on gold), not its strength. **Kept: nn3g500** (Mart, 2026-10-28) — equal strength, and its
self-play keeps gold victories in the data so later networks can learn the race themselves.
(Reversed after iteration 6: the race term is dropped again, see below.)

Iteration 4 (2026-10-29): `nn4_m10_1p5m.bin`, boards 70500–84499, driven by nn3g500 (gold wins
26.5% of self-play, vs 9% for nn2's). `m10_nn4` (all 9.3M, race residual 500, best epoch 24):
holdout 78.4% / 57.6%; **vs nn3g500 +16 ± 17** (nn4 320 gold wins, nn3g500 190), **vs hand-written
+466 ± 35**, 46 gold losses to it (5 after scoring itself ≥ +15). Small gain, within error.

Dropping the hand-eval data (2026-10-29): `m10_nn4w` on `nn*_m10_*.bin` only (6.1M, best epoch
36) is **level with nn4 (−4 ± 17)** but weaker vs hand-written (+420 vs +466; 56 gold losses): the
hand-eval games mainly teach how to beat the hand-written eval. Unlike nn2w (−29 with half of
nn2's data), equal-sized network data now matches all data, so a sliding window over network
data is viable from the next iteration. Gate on the head-to-head match, not vs hand-written.

Iteration 5 (2026-10-29, sliding window): `nn5_m10_1p5m.bin`, boards 84500–98499, driven by nn4;
`m10_nn5` on `nn[2-5]_m10_*.bin` (best epoch 33). **vs nn4 +7 ± 17, vs hand-written +456 ± 34**
(47 gold losses, 4 confident). Plateau: last gains +16, +7, both within error. Gold victories are
growing: 9% of nn2's self-play, 26.5% of nn3g500's, 34.3% of nn4's; nn5 vs nn4 ended on gold in
643 of 1600 games. The race term (500) plus self-play may be steering play toward races.
Direct **nn5 vs nn3 +33 ± 17** (chained estimate was +16): progress is real, ~+15/iteration vs
+50–60 before. But it comes from the race: nn5 wins 452 on gold (nn3 89), while nn3 mates nn5
604 times (nn5 mates nn3 415).

Iteration 6 (2026-10-29), **race term removed**: `nn6_m10_1p5m.bin`, boards 98500–112499, driven
by nn3 (13.2% gold wins in self-play, vs 34% for nn4's); `m10_nn6` on all 10.8M (`--dense 4`, no
race term, best epoch 38). **vs nn3 +80 ± 17, vs nn5 +31 ± 17, vs hand-written +521 ± 41**; gold
losses to hand-written 46 (5 confident) — as few as the race-term networks, without the term
(the race-heavy nn4/nn5 data is in its training set). nn5 still wins 383 on gold vs nn6, but nn6
mates it 744 times. Conclusion: the race term caused the plateau (races replaced learning the
game). Train without it; v1 inputs.

**Equal-time match (2026-10-29):** nn6 vs hand-written, 50 ms/move each (native AVX2 build, 6
threads = physical cores), 800 holdout boards × 2: **1451–146–3 (90.8%), +397 ± 30** (equal nodes:
+521). Avg depth 6.0 (network) vs 6.5 (hand-written). `engine/testdata/games_nn6_vs_hand_50ms.jsonl`.
On the ladder (hand-written 50 ms ≈ Level 6, ~1800; 500 ms was +521 over it) the network at 50 ms
is roughly Level 7 strength — before WASM SIMD, so the browser gap will be smaller until then.

Iteration 7 (2026-10-29): `nn7_m10_1p5m.bin`, boards 112500–126499, driven by nn6 (18.5% gold
wins in self-play, up from 13.2%); `m10_nn7` on all 12.3M (best epoch 32). **vs nn6 +24 ± 17,
vs hand-written +486 ± 36** (47 gold losses, 8 confident; 20 draws at the 400-ply cap). Racing
again without any race term: nn7 wins 366 on gold vs nn6 (nn6: 101) while nn6 mates nn7 642
times (nn7 mates nn6 447) — same pattern as nn5 vs nn3. So the race drift is not only the race
term: racing is how a new network beats the previous one (nn6 was weak against races; nn5 beat
it 383 times on gold). Possible cycle between racers and maters.
Checked: **no cycle.** nn7 vs nn5 +75 ± 17, vs nn3 +92 ± 18 (chained: +55, +104), so progress is
transitive. But ~45% of nn7's wins vs older networks are gold wins (444 of 1007 vs nn3), and nn3
still mates nn7 517 times: the networks increasingly win by racing.

**Gating from iteration 8 on (Mart, 2026-10-29):** no more matches vs the hand-written eval (saturated
at ~93%, says little); gate against earlier networks instead.

Overnight iterations 8–10 (2026-10-29/30; promote only at ≥ 50% vs the previous best; nn3 as fixed
reference). Style swings between rounds: nn8 beats nn7 by mating (821 mates; nn7 332 gold wins) and
nn8's own self-play has only 6.8% gold wins; nn10 beats nn8 by racing (579 gold wins; nn8 695 mates).
Strength vs nn3 still climbs: nn7 +92, nn8 +128, nn9 +113, nn10 +158. nn3 mates each of them
~360–520 times; 64% of nn10's wins vs nn3 are gold wins. Training time grows with the data
(nn10: 2 h 24 min on 17M); consider a window again if that becomes the bottleneck.

Why the swing: not the newest batch (only ~10% of the data) — nn9 and nn10 had almost the same
data and opposite styles vs nn8 (139 vs 579 gold wins), so likely training noise on a knife-edge
gold-vs-material trade-off. Replayed nn10 vs nn8 (mines / gold / material per side): identical
through ply 40; from ply ~60 nn10 banks gold (ply 80: 58 vs 41 gold, 3.1 vs 2.7 mines, more
material too). Its gold lead starts at ply 95 (median; p10 62) of its gold wins, so no racing from
the opening. But some losses show hoarding: mated with 160–230 gold unspent (#147: 193 gold, no
pieces left). Six games in Mart's library for review ("nn10 vs nn8 #…").

Quick check nn10 vs hand-written (2026-10-30, 150 boards × 2): 12k nodes 235–8–57 (+343 ± 46),
50 ms 220–19–61 (+282 ± 43). Fewer losses than nn6 (2.7% vs 4.7% at 12k; 6.3% vs 9.1% at 50 ms) but
~20% draws, all at the 400-ply cap with nn10 far ahead (its score ≈ +1500, i.e. ~99.7% win
probability) and not finishing — matching Mart's "strange moves when already winning". Value
saturation: once every move keeps the win probability near 1, the net can't tell them apart.
Draws vs hand-written grew with the iterations: nn6 3, nn7 20 (of 1600), nn10 ~19%.

## Moves-left head (2026-10-30)

Fix for the saturation above, after Leela Chess Zero's moves-left head. A second output predicts
the plies until the game ends; in clearly decided positions the search prefers a shorter game
when winning and a longer one when losing.

- Labels: plies left = last ply of the game − ply + 1, from the records' `ply` and `game`
  (`train.py plies_left`); games without a result (ply cap) are masked out. No new data needed.
- Model (`--moves-left`): branch on the first dense layer's outputs, Linear(l2→l3) → clamp →
  Linear(l3→1), output in units of 100 plies (`ML_SCALE`); MSE loss × `--ml-loss-weight` (0.05).
  `--init RUN` starts from a trained run (keeps its K); `--freeze-trunk` trains only the head, so
  the value output stays bit-identical to the init network.
- File format v4 (`export.py`, `nnue.rs`): v1 + f32 `ml_weight`, `ml_cap` after K, head weights at
  the end. `export.py --ml-weight W --ml-cap C` sets the defaults stored in the file (0 = off).
- Search (`Network::combine`): eval = logit·K − sign · min(conf · W · plies, cap, |eval|/2), with
  conf fading in from 90% to 99% win probability (|2p−1| 0.8 → 0.98), plies clamped to 0..300.
  W = eval units per ply (1 gold = 10). Never changes who is ahead. Override per engine with
  `gg ... --ml-weight W --ml-cap C` (`--a-ml-weight` / `--b-ml-weight` in matches).
- `gg nncheck` also checks the head (f32 path exact, quantised path within ~0.2 plies).

First test (`m10_nn10ml`: head fine-tuned on frozen nn10, all 17M positions, 2 epochs ≈ 25 min):
moves-left error 23.5 plies (always-the-median baseline 34.9). vs hand-written, 12k nodes, 150
boards × 2 (cap 150):

| weight (eval/ply) | W–L–D | Elo | median plies of its wins |
|---|---|---|---|
| 0 (= nn10) | 235–8–57 | +343 ± 46 | 150 |
| 1 | 250–15–35 | +366 ± 54 | 126 |
| **3** | **273–10–17** | +473 ± 75 | 116 |
| 10 | 233–16–51 | +318 ± 46 | 151 |

Weight 10 does nothing because the cap (150) is reached from 15 predicted plies on, so every move
gets the same adjustment; weight × typical plies must stay below the cap. vs nn10 (800 boards × 2):
weight 1 −1 ± 17, weight 3 −9 ± 17 — no measurable cost (it only acts in decided positions).
**Adopted (Mart, 2026-10-30): weight 3, cap 150** — `export.py` now stores weight 3 by default;
`m10_nn10ml/net.ggnn` (nn10 + head) is the current network. Future runs: train with `--moves-left`.

## Colour balance (recorded from 2026-10-30)

`gg match` now prints White's win rate (each board is played with both colours, so engine
strength cancels out). Random opening moves were removed from `gendata` on 2026-10-30 (Mart:
pointless in this game; only 0.7% of positions were random deviations, but the first 0–8 plies
were also searched with only a quarter of the nodes). Record White % for every new self-play
file and gating match here, to see whether the self-play figure moves now.

| Source (Black gold 6) | White % |
|---|---|
| self-play nn1–nn10 data (random openings, training boards) | 44.3–45.4% (every file) |
| gating matches nn3…nn10 (22 × 1600 games, same 800 holdout boards) | 43.9–49.1%, mostly 46–48% |
| nn10ml vs itself, 1200 holdout boards (1 game each) | 48.0% ± 2.8 (5 gold: 49.2%, 4 gold: 54.8%) |

**Black starts with 5 gold from 2026-10-30** (and no random openings): record new figures as
"Black gold 5". `gg match` prints the starting gold with the White %.

Fixed 2026-10-30: the train/val split hashed the file's index in the sorted file list, so adding
`nn10_m10_*` (sorts before `nn1_`) reshuffled other files' games between train and val. Harmless
for from-scratch runs, but a run continued from an older network validated partly on games that
network trained on (e.g. `m10_nn10c` from nn9: val loss 0.0165 vs nn10's 0.0177 — leaked, judge
it by the match). The split now hashes the file name.

## Next steps

1. Equal-**time** match (that is what matters in the browser); then speed work if needed:
   integer quantisation + SIMD (also `wasm32` simd128), or a smaller first layer.
2. Keep iterating network-driven self-play (see above); consider a sliding window that drops
   the oldest hand-eval data, and deeper labels.
3. Browser: ship `net.ggnn` with the client, load it into the WASM engine, re-set the computer
   levels' node budgets.
