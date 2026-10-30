# Current Work & Status

_Last updated: 2026-10-28_

## Current focus

**Reconnecting (2026-10-30).** `client/src/lib/hub.js` owns the game-hub connection: retries
forever (0/1/2/5 s), and when the page becomes visible again (phones kill sockets in the
background) it reconnects at once, or pings (`GameHub.Ping`, 1.5 s timeout) and restarts if
the socket is silently dead. Server keep-alive 5 s, client server-timeout 15 s. GamePage
joins via `onConnected` (runs after every reconnect) and removes its hub handlers on leaving.

**My library (2026-10-28).** Logged-in users save games with their variations: "📚" next to 🔍
after a game (GamePage; again = update the same entry), "Save to library" on the analysis board
(updates the entry it was opened from). `/library` page (nav: between Analyze and Rules) lists
entries; click opens them on the analysis board, ✕ removes. Server: `LibraryGame` table (migration
`AddLibraryGames`),
`LibraryController` (`/api/library`). Entries store positions, not just moves (client packed
format, `gameTransfer.js` `packGameTree`/`packAnalyzeTree`), so they survive rule changes.
`tools/LibraryImport` replays seed + moves (e.g. `gg match --out`) with the server rules and adds
them to a user's library.
Sharing (2026-10-29): each entry has a `ShareId` GUID and `IsPublic`; the analysis board's URL is
`/analyze/{shareId}` for library entries. "Publish" switch + "Copy link" under Library. Published
entries open for anyone (`GET /api/library/shared/{shareId}`, anonymous); non-owners can play on and
"Save to my library", which adds a copy (new GUID, URL changes) — only the owner updates the original.
All library migrations are on production (2026-10-29, via deploy.py's `db-migrations/` step).

**Rejected 2026-10-27: mine upgrades.** Tried as a test rule (spend the turn + 40 gold to raise a
mine's income from +3 to +5, once per mine, reset on capture) in engine, server and client, then
removed completely. Mart, after playing it: not strategically relevant, just design bloat. Turtling
and upgrading doesn't work, the opponent simply upgrades too and wins the gold race first. Engine
data agreed it adds little: at 20–40 gold the engine (eval value 1.5x a mine) almost never upgrades
and forcing it loses Elo; at 10 gold it becomes a must-buy that turns games into a pure gold race
(gold wins 76% vs ~55%, games ~20% shorter).

**Rule change: queen drops — adopted as the standard rules on 2026-10-26** (see game-rules.md).
Mart found attacking rarely viable without a big advantage, and queens weak. Now R/B/N may
also be placed next to your queen, and a queen costs 75. Implemented everywhere: C# server,
Rust engine (default `Rules`; eval queen value 750), JS rules (`analysis.js`, `constants.js`),
Python (`policyNetwork/`, since removed), rules page. Old rules: `Rules::classic()`, `gg --classic`.
Note: computer level Elos, bidding calibration and the colour balance (Black 6 gold) were
measured under the old rules and may need re-checking.
Engine self-play comparison: `engine/tools/compare_selfplay.py` on `testdata/qd_base.jsonl`
vs `qd_var.jsonl` (1000 games each, 60k nodes, mirrored 10×10). Results (standard → variant):
- Checkmates 33.6% → 47.3% of games, gold victories 62.3% → 49.0%; mates before either side
  has 120 gold 29.2% → 42.5%. Winner's final gold 160 → 140.
- Colour balance unchanged (White 43.4% → 44.3%); length unchanged (median 114 → 112 plies).
- Queens bought less (1.04 → 0.65 per game, in 63% → 52% of games, first at ply 76 → 86) but
  lost far less (0.55 → 0.20 per game). Queen drops: 1.18 per game (5.8% of non-pawn buys).
- Checks and captures per 100 plies unchanged (12.5 → 12.8, 19.3 → 19.6).
- That run still valued a queen at 72 in the eval while it cost 75. Fixed (eval queen value now
  follows `rules.queen_cost`) and rerun as `qd_var2.jsonl` (standard → variant): checkmates
  33.6% → 53.6%, gold victories 62.3% → 43.0%, mates before 120 gold 29.2% → 48.1%; White
  43.4% → 45.8%; queens 1.04 → 0.84 per game (in 63% → 62% of games, first at ply 76 → 80),
  lost 0.55 → 0.28; queen drops 1.60 per game (7.9% of non-pawn buys); median length 114 → 111.

**Evaluation-network training — environment set up 2026-10-26** (see training.md): `gg gendata`
writes labelled positions from engine-vs-engine games, `training/` trains an NNUE-style value
network on CPU. Dev run (2,000 games): val winner prediction 74.1% vs 76.5% for the engine's
own search score. First real run (Mart, 2026-10-27): 1M positions at 20k nodes (`m10_1m`, 567k
train positions). On 600 unseen games (`training/evaluate.py`, `holdout_20k.bin`) it is roughly on
par with the hand-written eval: winner 77.4% vs 76.1% (close positions 56.1% vs 53.9%), gap to
search 6.8 vs 6.0 points. Rust inference done (`engine/src/nnue.rs`, exact vs PyTorch, 3.9x fewer
nodes/s). **Equal-nodes match (12k nodes, 200 holdout boards x 2): network 294 - hand-written 106,
73.5%, +177 ± 39 Elo.** 3M positions (`m10_3m`, m10_1m + m10_2m, 28.5k games): holdout winner
77.9% (close 56.8%), barely above 1M (the search itself only gets 79.3%, so this metric is near
its ceiling; train/val split from epoch ~5 shows it memorising games). But in play, at 12k nodes
on 400 holdout boards x 2: **3M vs 1M +137 ± 26 Elo, 3M vs hand-written +287 ± 33 Elo**
(`engine/testdata/match_nnue3m_*.log`). Data volume matters a lot for strength.
**Iteration 1 (network-driven self-play):** `gg gendata --nnue m10_3m` → `nn1_m10_1p5m.bin`
(14k games, boards 28500–42499, 1.55M positions, 65 min); `m10_nn1` trained from scratch on all
4.76M positions. Holdout metrics flat (77.8% / close 56.3%; holdout games are hand-eval games),
but in play (12k nodes, 800 holdout boards x 2): **nn1 vs 3m +121 ± 18, nn1 vs hand-written
+359 ± 27** (`engine/testdata/match_nnue_nn1_*.log`). Self-play iterations work; judge by matches,
not the holdout. Iteration 2 (`nn2_m10_1p5m.bin`, boards 42500-56499, driven by nn1; `m10_nn2` on
all 6.3M): **nn2 vs nn1 +50 ± 17, vs hand-written +433 ± 32** (hand-written now almost never
reaches a gold win). Gains shrinking. Sliding window (network data only, `m10_nn2w`) vs nn2: −29 ± 17, so keep all
data for now. Iteration 3 (`nn3_m10_1p5m.bin`, boards 56500-70499, driven by nn2; only 9% gold
wins in that self-play): **nn3 vs nn2 +60 ± 17, vs hand-written +435 ± 32** (same as nn2). Loop
stopped here (Mart). Gold-race fix: fixed race term in the output (`--race-residual 500`, v3 file), level with nn3,
+461 vs hand-written, fewer gold losses; kept so gold wins stay in self-play data. Iteration 4
(driven by nn3g500, 2026-10-29): **nn4 vs nn3g500 +16 ± 17, vs hand-written +466 ± 35**. Network-only
data matches all data (nn4w −4 vs nn4), so iteration 5 used a sliding window (nn2–nn5 data):
**nn5 vs nn4 +7 ± 17** — plateau; gold victories rising (34% of self-play). nn5 vs nn3 +33
(all from gold wins). Race term removed in iteration 6 (data by nn3, all 10.8M, v1 inputs): **nn6
vs nn3 +80, vs nn5 +31, vs hand-written +521**, gold losses as low as with the term. Equal time (50 ms,
native): **nn6 vs hand-written +397 ± 30**. Iteration 7 (by
nn6): **nn7 vs nn6 +24 ± 17, vs hand-written +486**, but the gain is from gold races again (no race
term). Iterations 8–10
overnight: **nn10 vs nn8 +26, vs nn3 +158** (nn9 not promoted). Best net:
`training/runs/m10_nn10/net.ggnn` (see docs/training.md).
nn10 draws ~20% vs hand-written at the 400-ply cap while far ahead (value saturation) →
**moves-left head** (2026-10-30, training.md): head fine-tuned on frozen nn10 (`m10_nn10ml`),
draws vs hand-written 57 → 17 of 300 at weight 3, no cost vs nn10. **Adopted: weight 3**; current
network `training/runs/m10_nn10ml/net.ggnn`. Future training runs use `--moves-left`.

**Agreed plan (in this order):**
1. *(Done 2026-10-30: White 48.0% at 6, 49.2% at 5, 54.8% at 4 — 4 overshoots; see engine.md.)*
   **Adopted: Black starts with 5 gold** (Mart, 2026-10-30): server `PieceColor.cs`, engine
   `Position::empty` + `gg --black-gold` default, analysis board, rules/about pages, game-rules.md.
   Oracle data regenerated (`oracle.jsonl`, 0 mismatches; the test now takes the starting gold from
   each recording, older files keep 6). Bidding is unchanged (Black gets the winning bid).
   **Black starting gold 4?** nn10 vs itself at `--black-gold` 6 / 5 / 4 (1200 boards, 12k nodes,
   count only the A-as-White game per board — identical engines replay the same game). Network
   self-play currently ends ~55% Black. Rule change would touch C#, Rust, JS, game-rules.md and
   check `computerBid` defaults; existing training data stays usable.
2. **Continue vs from scratch:** train from nn9's weights (`--init m10_nn9`, lower lr, few epochs)
   on exactly nn10's data and match vs nn10 (from scratch). If level, continued training becomes
   the default (≈2.4 h → ~30 min per iteration), with an occasional from-scratch run.
Later: browser integration (WASM simd128 first).

**Server scaling review (2026-10-27).** Server CPU load is light (the computer opponent runs in
the browser); a 2 vCPU / 4 GB VPS should carry thousands of DAU. Done: per-game lock
(`ServerGame.LockAsync()`), taken by every `GameHub` method and controller read that touches a
live game, so concurrent calls from both players/spectators can't interleave (e.g. a move racing
`ClaimTimeout`). Stale games fixed: `GameCleanupService` (every minute) ends timed games whose
flag fell server-side (nobody claimed it), ends untimed games abandoned 7 days as `"abandoned"`
(no Elo), and every 30 min deletes orphaned in-progress `GameSessions` rows. End-of-game
broadcast/persist moved from `GameHub` to `GameFlowService` so the cleanup can use it. Watch
page: only started, public, unfinished games, top 30 by rating, plus a total count.
Open, in rough priority: drain mode for deploys (live games are in-memory
only; a restart ends all non-slow games); compute legal moves client-side instead of per-click
hub calls; cache `/replay` for finished games; DB indexes for profile/leaderboard queries;
Cloudflare in front (TLS, compression, caching); rate limiting on auth/game creation; backups.

**Draw offers + game panel (2026-10-28).** Positions where endlessly buying/exchanging is best for
both sides came up vs the engine, so: draw by agreement (result `"draw"`, ½ for Elo; see
game-rules.md). Hub: `OfferDraw` / `AnswerDraw` (human games, `DrawOfferChanged` event, offer
also in the state DTO), `AgreeComputerDraw` (computer accepts unless > 10 gold ahead, decided in
the browser). Game page side panel redesigned: move list ~80%, then a fixed action section
(Resign / Draw at the top, rematch there after the game; Analyze / Save at the bottom), with
monochrome SVG icons. Untested in a live game so far.

**Earlier focus: computer opponent.**
**Computer opponent.** Question being investigated: is it realistic to generate the first NN
training games with the brute-force engine? Known problems:

1. Far too slow at higher depths and/or with many pieces on the board.
2. It does not know how to end the game: it keeps buying pieces and makes no attempt at a
   checkmating attack. Games would be low quality and slow to generate.

## Where the engine lives

- `client/src/lib/analysis.js` — negamax alpha-beta + Zobrist TT, eval, move gen, make/unmake.
- `client/src/lib/pathFinder.js` — JS move generation / check detection.
- `client/src/lib/analysisWorker.js` — continuous iterative-deepening analysis (Web Worker).
- `client/src/AnalyzePage.svelte` — "Bot move" button calls `getBestMove` (≈3M playouts budget).
- No server-side bot; no "play vs computer" game mode yet.

## Engine review (2026-10-24) — why it is weak/slow

### Why it never goes for mate
- **Checkmate and stalemate are not scored.** In `negamax`, a node with no legal moves returns
  the static eval (`legalMoves.length === 0 → return evalScore`). Mate looks like an ordinary
  position, so the search has no reason to seek it. (The ±999 king-missing term never fires,
  because legal moves never allow king capture.)
- **Buying is always "profitable" in the eval.** Piece values (Q90 R52 B42 N40 P25) exceed their
  costs (72/40/32/30/26) and unspent gold counts 1:1 with material, so every purchase gains score.
- **No positional terms**: no king safety, attack on the enemy king, mobility, or piece activity.
  Nothing pulls pieces toward the enemy king.
- Gold victory and the stalemate-win rule are not in the search either.

### Why it is slow
- **Root moves are searched with a full window** (`-Infinity, Infinity` per root move in
  `getTopMoves`/`getBestMove`), so there's no pruning at the root.
- **No move ordering** (no TT best move, captures first, killers/history), so alpha-beta prunes
  poorly. No quiescence search, no iterative deepening in `getBestMove`.
- **Huge branching factor from placements**: up to 5 piece types × every empty square around the
  king (+ pawns around every non-pawn piece).
- Allocation-heavy JS (new move objects per node), full-board scans in eval and move gen,
  `setTimeout(0)` yields every 1000 nodes (browsers clamp nested timeouts to ≥4 ms).
- Single-threaded, running in the browser.

### Bugs noticed
- `AnalyzePage.svelte` `botMove()` does not `await getBestMove(...)` (it is `async`), so
  `bestMove` is a Promise → the "Bot move" button likely misbehaves. _(unverified)_
- JS placements are not filtered for "leaves own king in check" (the server does filter).

## Neural network history (paused, last work 2026-03-28)

Code removed on 2026-10-26 (in git history; logging conventions kept in training.md).
Summary of what was tried:
- 10×10 boards, ResNet (15 blocks × 256 filters), policy head; trained on winner-only positions
  from self-play, draws discarded. Started from heuristic play (`config.py` heuristics).
- Value head tried and removed (worse); later re-added together with a small MCTS
  (10 simulations/move, last commit "MCTS + value head"). Uncommitted: `gamma` default 0.99 → 1.0.
- Lots of heuristic tuning to fight degenerate play (pawn spam, no queens, draw loops).
- Outcome: never close to good enough.

## Requirements (decided with Mart, 2026-10-24)

- **Website bot runs in the client** (saves server compute). Thinking time configurable by the
  user.
- **Training-data engine** may be a separate, speed-first implementation in another language,
  but Mart must be able to **test it on the dev site**.
- The bot serves **both** purposes: playable opponent and training-game generator.
- **Board size: 10×10** is the target.
- Training games: fast, but not so fast that quality collapses — tune by testing.
- Compute: test at home first (dev machine: 12 logical cores, no NVIDIA GPU detected); later
  rent cheap CPU/GPU on e.g. vast.ai.
- Stalemate rule clarified: no legal moves (incl. placements) = loss. Current code is wrong —
  see [game-rules.md](game-rules.md).

## Progress (2026-10-24)

- ✅ Step 1: stalemate rule fixed in C# (`Game.cs`), rules page text clarified.
- ✅ Step 2: Rust engine core, validated against the C# rules on 219k positions (0 mismatches).
- ✅ First versions of steps 3–5: search, eval, `gg selfplay` / `gg match` harness.
  The engine now plays games to a finish (mate or gold victory). Details and benchmarks:
  [engine.md](engine.md).
- ✅ Step 6 (first version): engine compiled to WASM; "Play vs computer" on the analysis page
  (`AnalyzePage.svelte`). Verified in Node (wasm + page bookkeeping, full games); **not yet
  clicked through in a browser** — Mart is testing it on the dev site to judge strength.
- ✅ "Analyze" button switched to the new engine (multi-PV 5).
- ✅ Strength ladder measured (2/5/10/50/500 ms ≈ 2.4k/6k/12k/60k/600k nodes); see engine.md.
- ✅ (2026-10-24) **Play vs computer** tab on the home page (6 node-based levels, Elo
  estimates 700–2200), stored in history as unranked. *Ranked* checkbox on Create lobby.
  Analysis page: play-vs-computer removed; "Engine" section with Analyze + Engine move, top
  move shown on the board. **Not yet tested end-to-end in a browser** (server restart needed).
- ✅ (2026-10-25) Computer bidding finalised (5 passes, fractional gold, high-bid correction;
  see engine.md) and eval tempo set to 0. Ready to deploy.
- ✅ (2026-10-25) **New board look implemented** (`client/src/lib/boardTheme.js`, `Board.svelte`):
  SVG cburnett pieces, Twemoji mountains/pines, gold game-icons mines/chests, mine owner badge.
  Board theme chosen in the ⚙ menu (`settings.boardTheme`, localStorage `aoc_boardTheme`): Maple &
  walnut (default; texture at 35%), Ash & teak, Classic brown, Parchment, Green, Slate.
  Mountains: Twemoji 26f0 plain mountain at 0.92 (the snowy peak stood out too much). Old
  sprites removed; credits on the About page. Still old: `assets/other/tutorial_board.png`
  (Rules page screenshot).
- ✅ (2026-10-25) **Site restyle** to match the board: warm dark palette + gold accents and
  Alegreya SC (headings, logo) / Alegreya Sans (body). Colours are CSS variables defined in
  `App.svelte` (`--bg`, `--surface*`, `--line*`, `--fg*`, `--muted`, `--faint`, `--accent*`,
  `--button*`, `--font-display`, `--font-body`); components use these instead of literals.
  Kept on purpose: profile graph category colours, gold amounts, red errors, green "your turn".
- ✅ (2026-10-26) Open lobby visibility: unranked + public → everyone (also logged out);
  ranked + public → logged-in users; private → nobody (invite link only). Lobbies created
  without an account and computer games are never listed. `POST /api/game/{id}/join` now
  applies the same rules (it used to hand out the black token of any game, incl. private and
  running ones). `GameController.LobbyVisibleTo`.
- ✅ (2026-10-25) Game page fits the window height: the layout width is computed so the board
  fills the height left by the panels (`fitBoard()` in `GamePage.svelte`; 1440p ~990px, 1080p ~690px).
  Status messages (result, connection lost, invite link) float over the top of the board.
- Visual update exploration: higher-res (SVG) pieces and wooden board textures. Preview
  page with candidate styles: https://claude.ai/artifact/MnauqiVQuAhpeA7sspaJ9E (private).
  **Chosen default theme (2026-10-25)**, to implement in Board.svelte: icon size Large (×0.9);
  cburnett SVG pieces (no outline on black pieces); squares "Ash & teak" (ambientCG Wood048
  light / Wood030 dark, random crop per square, odd files rotated 90°); mountains = Twemoji
  1f3d4 snowy peak (size 1.0, raised 0.035 sq); forest = two Twemoji 1f332 pines (0.70 at
  −0.22/−0.06, 0.78 at +0.21/+0.06; trunk recoloured #662113 → #a8682f); income squares
  ("mines") = own Twemoji-style drawing `terrain/mine.svg` (2026-10-26: dark rock mound in the
  mountain greys, timber-framed tunnel, gold nuggets; the SVG is the source,
  option "C2"; replaced the game-icons gold-mine glyph), drawn exactly like the mountain (0.92, raised 0.035) so the grass lines up; treasure
  = game-icons lorc/locked-chest in gold #e0ae37 (was #ecbb3c until 2026-10-26). Emoji get a soft drop shadow. Mine owner
  badge: disc top-right (white/black, gold rim) with "+3" centred on its measured glyph box.
  Credits needed: Twemoji (CC BY 4.0), game-icons.net (CC BY 3.0), cburnett (GPLv2+).
- ✅ Bidding vs the computer (computer bids 5 + its evaluation of White's advantage), tested
  end-to-end against the server both ways. Also fixed: `SwapColors` didn't swap player names,
  so after a bidding swap in human games both names were shown on the wrong colours.
- ⏭ Next: collect Mart's feedback on strength/weaknesses; tune eval/search with matches;
  decide training-data format (step 7).

## Running locally ("dev site")

There is no hosted dev site; `deploy.py` only deploys production. Locally:
- `dotnet run --project AgeOfChess.Server --launch-profile https` → https://localhost:7074
  (serves the built client from `wwwroot`; needs the dev MySQL at 192.168.1.202).
- For client hot-reload also run `npm run dev` in `client/` → http://localhost:5173
  (proxies `/api` and `/hub` to :7074).
- Nothing committed yet (new: `CLAUDE.md`, `docs/`, `engine/`, `tools/RulesOracle/`).

## Plan

One engine written in **Rust**, compiled two ways:
- **native** (multi-threaded, one game per core) → training-game generator, cheap to run on
  rented CPUs;
- **WebAssembly** → runs in the browser as the website bot, so the exact same engine is
  testable on the dev site.

Steps:
1. Fix the stalemate rule in C#/JS/Python (small, independent).
2. Rust engine core: compact board (arrays, no allocations per node), move gen incl.
   placements, make/unmake, Zobrist. Validate move gen against the C# rules (perft-style
   comparisons on recorded games).
3. Search: iterative deepening with time limit, aspiration/PVS, TT best move, move ordering
   (captures, checks, killers/history), quiescence, correct mate/stalemate/gold-victory scores.
4. Eval: buying must not be free (value pieces ≈ cost; reward income/mines, mobility, king
   safety, attackers near the enemy king, gold-victory proximity).
5. Benchmarks + self-play match harness (engine version A vs B) to tune strength vs speed.
6. WASM build + wire into the client (Web Worker), "play vs computer" mode with time setting.
7. Generate games for NN bootstrapping; revisit `policyNetwork/`.

Toolchain: Rust 1.99 (GNU host, + `wasm32-unknown-unknown` target) installed 2026-10-24;
`wasm-pack` not yet installed.
