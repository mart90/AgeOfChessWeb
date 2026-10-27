# Rust Engine (`engine/`)

One engine, two targets: **native** (multi-threaded training-game generation) and, later,
**WebAssembly** (website bot running in the browser).

## Layout

| File | Purpose |
| --- | --- |
| `src/position.rs` | Board (≤16×16 mailbox + neighbour tables), make/unmake, Zobrist, attacks, game-end rules |
| `src/movegen.rs` | Legal moves + placements, tactical-only generation, perft |
| `src/seed.rs` | Load a board from a server map seed (`m_10x10_...`) |
| `src/eval.rs` | Evaluation with tunable `EvalParams` (JSON-serialisable) |
| `src/search.rs` | Iterative deepening PVS, aspiration windows, TT, null move, LMR, LMP, reverse futility, quiescence (captures/treasures/mines), check extension |
| `src/clock.rs` | ms clock for native + wasm |
| `src/bin/gg.rs` | CLI: `bench`, `selfplay`, `match`, `play` |
| `tests/oracle.rs` | Validates rules against the C# server |
| `params/*.json` | Eval parameter sets for matches |
| `testdata/seeds_m10.txt` | 2000 mirrored 10×10 seeds from the server's MapGenerator |

## Browser (WebAssembly)

- Built as a plain `wasm32-unknown-unknown` cdylib (no wasm-bindgen/wasm-pack). Exports:
  `alloc`, `dealloc`, `handle(ptr, len)`, `out_len`; imports only `env.now_ms`.
- JSON API in `src/api.rs` (`search`, `legal`, `status`, `newGame`); positions use the
  client's square format. `src/wasm.rs` is the raw export layer.
- `npm run build:engine` (in `client/`) rebuilds and copies the binary to
  `client/src/lib/engine/goldrush_engine.wasm`. **The .wasm is committed**, so the server's
  normal `npm run build` / `deploy.py` needs no Rust toolchain.
- Client side (`client/src/lib/engine/`): `engineWasm.js` (loader), `engineWorker.js`
  (searches off the main thread), `engine.js` (main-thread instance for legal moves/status +
  worker wrapper with cancel).
- Speed in V8 ≈ 1.2M nodes/s (close to native single-thread).
- **Analysis page "Engine" section**: *Analyze* (`engineAnalysis.js`: depth-stepped searches,
  multi-PV 5 = repeated root searches excluding earlier best moves; top move drawn on the
  board as an arrow / ghost piece) and *Engine move* (plays the top move while analyzing,
  otherwise searches 600k nodes). Typical 10×10 midgame: depth 8 ≈ 0.5–1.5 s, depth 10 ≈
  2–15 s. The old JS `lib/analysis.js` is only used for sandbox placement helpers.

## Play vs computer (home page tab)

- Levels (node budget per move → same strength on every device; Elo = Mart's estimates,
  set by Mart on 2026-10-26): 1: 1k + mistakes ~800 · 2: 1k ~1000 · 3: 2.4k ~1200 · 4: 6k ~1400 ·
  5: 12k ~1600 · 6: 60k ~1800 · 7: 600k ~2000. Defined in
  `AgeOfChess.Server/Services/ComputerLevels.cs` and `client/src/lib/engine/levels.js` (keep in sync).
- **Level 1 mistakes:** search the two best moves (multi-PV 2, 1k nodes each) and with
  probability 0.6 play the second if it is at most 5 gold worse (`ComputerMistakeRate/MaxLoss`
  in the game state; chosen in `GamePage.chooseWithMistakes`). Tuned with `gg match
  --a-mistake 0.6,5 --nodes 1000` vs Level 2: 19.9% (−242 ± 19, 2000 games) → ~650.
  Other settings tried (1000 games each): 0.3/3g −129, 0.5/3g −193, 0.3/6g −159, 0.6/4g −233,
  0.7/3g −225, 0.6/6g −265. (Fixed on the way: multi-PV stopped after the first line under a
  node limit.)
- `POST /api/game/computer {boardSize, mapMode, mapSeed, level, color}` creates a normal
  server game (always unranked, untimed, private) with `GameSettings.ComputerLevel` /
  `ComputerIsWhite`. The computer side counts as connected (`ServerGame.SideReady`).
- The human's `GamePage` runs the engine in a Web Worker on the computer's turn and sends the
  move with hub method `SubmitComputerMove(humanToken, ...)`; the server validates it like any
  move. The computer's token is never handed out. Min. 400 ms per computer move for UX.
- **Bidding vs the computer** (optional): the human starts as the "creator" (White token).
  **Live algorithm (2026-10-25)**, `client/src/lib/engine/computerBid.js` (Rust copy for tests:
  `engine/src/bid.rs`): budget = level nodes × 30, 5 passes using 30/25/20/15/10% of it. Each
  pass analyses the start with Black holding the current estimate (rounded to 0.1 gold;
  fractional gold ticks up 0.1 per Black turn until whole, `Position::frac`); result r =
  White's remaining advantage in gold; estimate += sign(r)·k·ln(1+|r|) with
  k = 1.5, 1.25, 1, 0.75, 0.5 (mirrored) or 5, 4, 3, 2, 1 (full random). Start 5, no caps.
  High-bid correction: if the bid would be ≥ 7 (estimate ≥ 6.5), the estimate is reduced by
  round(estimate/5) (6.5–7.4 → −1, 7.5–12.4 → −2, …), then rounded (the only rounding of the bid). Level 6 takes ~30 s.

  Test history (`gg bidmatch`: both play at 12k nodes, each board twice alternating who wins ties):

  | Variant | vs | Full random | Mirrored |
  | --- | --- | --- | --- |
  | v1: 3 passes 50-30-20, k·ln(1+\|r\|), k=3-2-1, ×20 | fixed 6, all boards | 70.1% ± 2.8 | 51.0% ± 3.1 |
  | v2: k·ln(1+\|r\|/k), k=3-2-1 / 5-2.5-1 | fixed 6, all boards | 69.5% ± 2.9 | 52.7% ± 3.1 |
  | 5 passes, k=3-2.5-2-1.5-1 / 5-4-3-2-1 | fixed 6, all boards | 71.0% ± 2.8 | 54.4% ± 3.1 |
  | v2, ×20 | fixed 5, bids ≤3 or ≥7 | 73.3% ± 2.0 | 48.4% ± 2.0 |
  | 5 passes, ×30 | fixed 5, bids ≤3 or ≥7 | — | 48.0% ± 2.0 |
  | + mirrored k halved (with /k) | fixed 5, bids ≤3 or ≥7 | — | 49.4% ± 2.0 |
  | + no /k divisor, round only at end | fixed 5, bids ≤3 or ≥7 | 73.2% ± 2.0 | 48.3% ± 2.0 |
  | + fractional analysis gold | fixed 5, bids ≤3 or ≥7 | 72.9% ± 2.0 | 51.6% ± 2.1 |
  | live, but first 40 plies at 120k nodes | fixed 5, bids ≤3 or ≥7 | — | 50.6% ± 2.1 (White 43.9%) |
  | live, bid analysis with tempo 0 | fixed 5, bids ≤3 or ≥7 | — | 51.5% ± 2.1 (White 46.2%) |
  | **live**: tempo 0 everywhere + high-bid correction | fixed 5, bids ≤3 or ≥7 | **77.6% ± 1.9** (White 74.7%) | **52.8% ± 2.5** (White 49.2%) |

  On mirrored boards the bidder keeps scoring ~45–47% when it pays ≥7 for White and ~54–58%
  when it bids ≤3. Mart checked the extreme boards m_10x10_4m1m2r1f3f8t6r1rt4mf1k7 (bid ~20) and
  m_10x10_tr3r4f1m3m6t9fmr2f8k2 (~1) by hand: both look right (path blocking / exact gold
  for a knight + last treasure). Theory "the bid analysis sees deeper than the bot plays" was
  refuted: with a strong opening (40 plies at 120k) the bidder did worse as White (43.9%).
  More likely the eval overrates White on mirrored boards: median bid 7, while self-play puts
  the fair Black starting gold at ~4 (White 48.3% with 5 gold, 46.7% with 6).
  Submitted via hub method `SubmitComputerBid`. Normal resolution follows (creator wins ties); `SwapColors`
  also flips `Settings.ComputerIsWhite`, which is persisted to `SettingsJson`. Computer moves
  are rejected while bidding is open (`GameStateDto.BiddingInProgress`).
- Games are stored in history like any other (opponent shown as "Computer (Level N)",
  "unranked" in the rating column); resume after restart and replays work as usual.
- Ranked/unranked and computer info live in `GameSettings` (serialised in `SettingsJson`),
  so no DB migration was needed. `Create lobby` has a *Ranked* checkbox (default on);
  matchmaking games are always ranked; unranked games skip `ApplyEloUpdates`.

## Rules validation

`tools/RulesOracle` (C#) compiles the server's real `GameLogic/` sources, plays random games
and dumps every position's legal-move set, gold and result. `cargo test --release` replays
them in Rust. Last full run: **219,588 positions, 0 mismatches** (incl. 113 checkmates,
187 gold wins, 22 stalemates). Re-run this after any rule change.
Rule change 2026-10-26 (queen drops, queen 75): the oracle now writes `queenDrops: true`;
data without it (`oracle_classic*.jsonl`, recorded before) is replayed with `Rules::classic()`.
`oracle.jsonl` (committed) and `oracle_big.jsonl` were regenerated; last full run 352,234
positions (new + classic data), 0 mismatches. JS placement rules checked against the engine on
6,866 random positions, 0 mismatches.

```
dotnet build -c Release tools/RulesOracle
dotnet tools/RulesOracle/bin/Release/net9.0/RulesOracle.dll 600 12345 engine/testdata/oracle_big.jsonl 400
dotnet tools/RulesOracle/bin/Release/net9.0/RulesOracle.dll seeds 2000 10 m engine/testdata/seeds_m10.txt
```

## Scoring conventions

- Eval units: 1 gold = 10. Pieces valued at cost (Q750 R400 B320 N300 P260) so buying is
  neutral by itself; purchases must pay off via mobility, king pressure, mine control.
- Gold race term: `race * (1/(turns_to_threshold_me+1) - 1/(…opp+1))`.
- No legal moves (checkmate or stalemate) and gold victory = `±(MATE − ply)`, MATE = 1,000,000.

## Usage

Rust lives in `%USERPROFILE%\.cargo\bin` (GNU toolchain, no MSVC needed). From `engine/`:

```
cargo build --release
./target/release/gg bench --depth 6
./target/release/gg play --time 200                      # one verbose game
./target/release/gg selfplay --seeds testdata/seeds_m10.txt --games 48 --threads 12 --time 100 --out games.jsonl
./target/release/gg match --a default --b params/old_js_eval.json --seeds testdata/seeds_m10.txt --games 60 --time 100
```

Self-play output (JSONL): `seed`, `moves` (notation), `scores` (White's view, per move),
`depths`, `result` (`w+c`, `b+g`, `draw` = hit max plies, …), `plies`, `pieces_bought`,
`bought_by_kind` (per side, Q R B N P), `queen_drops` (non-pawn placements not next to the own
king), `checks`, `captures`, `queens_lost`, `first_queen_ply`, `final_gold`, `final_mines`.
Compare two runs: `python engine/tools/compare_selfplay.py A.jsonl B.jsonl`.

Training data for the evaluation network: `gg gendata` (see [training.md](training.md)).
Evaluation network in the engine: `src/nnue.rs`, `--nnue`/`--a-nnue`/`--b-nnue`, `gg nncheck`
(see training.md, "Network in the engine").

Rules: `Position.rules` (`Rules { queen_drops: bitmask, queen_cost }`), default = current rules.
CLI `--classic` (pre-2026-10-26 rules) or `--queen-drops LETTERS --queen-cost C`; browser API
override: `position.rules = { queenDrops: "", queenCost: 72 }`.

## Benchmarks (dev machine, 12 logical cores, 2026-10-24)

- Search ≈ 1.5M nodes/s single thread, ≈ 0.77M nodes/s/thread with 12 threads.
- Depth 6 on a 10×10 midgame: 5–50 ms. At 100 ms/move: depth ≈ 7–8 avg (14 in the opening).
- Self-play, 100 ms/move, 12 threads: **~2,500 games/hour**, avg 125 plies, all games decisive
  (48 games: 20 checkmates, 28 gold victories).
- **Colour balance (2026-10-24, after the gold-victory fix):** 1000 self-play games, 100 ms/move,
  1000 different mirrored 10×10 boards, Black starts with 6 gold: **White 46.7% ± 3.1%**
  (Black 53.3%, no draws; ≈ −23 ± 22 Elo for White). Results: gold 59.5%, mate 37.6%,
  stalemate 2.9%. Median 111 plies (p10 76, p90 181); ~15 pieces bought per side.
  Data: `engine/testdata/sp_1000_100ms.jsonl` (local, gitignored).
- **Black starting gold 5 vs 6** (same 1000 boards, 70k nodes/move ≈ 100 ms, `--black-gold`):
  5 gold → White **48.3% ± 3.1%**; 6 gold → White **46.7% ± 3.1%**. Difference (1.6 pp) is
  within noise; both lean slightly to Black. Resolving ±1 gold would need ~10k games each.
  Only 53% of boards had the same winner in both runs, i.e. outcomes are very sensitive.
- **Thinking-time ladder (2026-10-24).** Browser ≈ 1.2M nodes/s, so matches use node
  equivalents: 10 ms ≈ 12k, 50 ms ≈ 60k, 500 ms ≈ 600k nodes/move (`--a-nodes/--b-nodes`).
  Anchor: Mart estimates the 50 ms engine at **~1800** (beat it once).
  - 10 ms vs 50 ms: 7.4% (74–926, 1000 games) → −439 ± 42 → **~1360**
  - 500 ms vs 50 ms: 95.3% (381–19, 400 games) → +521 ± 86 → **~2320**
  - 5 ms vs 10 ms: 22.6% (226–774, 1000 games) → −214 ± 26 → **~1145**
  - 2 ms vs 5 ms: 14.6% (146–854, 1000 games) → −307 ± 31 → **~840**
    (5/2 ms runs after limits were made exact for small budgets: check every 128 nodes.)
  - 1k nodes vs 2.4k (Level 1): 16.6% (165–834–1 draw, 1000 games) → −281 ± 29
    → ~720 with 2.4k at 1000 (Mart's estimates, +100 on 2026-10-24). 1k became Level 1 (~700),
    the others moved up one level.
  - Engine-vs-engine gaps are usually inflated relative to games against humans.
  - Time-based levels vary by device (phones are slower); for public bot levels, node limits
    would give the same strength everywhere.
- **Tempo bonus** (eval `tempo`, 1 gold for the side to move; an unreviewed default): tempo 0
  vs 10 at 12k nodes, 500 mirrored boards × 2: **55.7% (+40 ± 22 Elo) for tempo 0** (2026-10-25).
  **Tempo is 0 since 2026-10-25**, so all bot levels are somewhat stronger than the ladder
  below was measured with (Elo estimates not yet re-measured).
- Default eval vs. old JS-style eval (same search, 100 ms): **+207 ± 76 Elo** (120 games).
  The old eval buys ~40% more pieces and games last longer (167 plies).
