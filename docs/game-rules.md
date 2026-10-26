# Goldrush Gambit — Rules & Design

Goldrush Gambit is a chess variant designed by Mart. It adds an economy (gold, mines, treasures,
buying pieces) and randomly generated terrain that blocks movement. Both players start with
**only a king**.

Source of truth for the rules is the server: `AgeOfChess.Server/GameLogic/` (mainly `Game.cs`,
`Map/PathFinder.cs`, `Map/MapGenerator.cs`). The player-facing text is in
`client/src/RulesPage.svelte` and `client/src/AboutPage.svelte`.

> The rules are implemented **three times**: C# (server, authoritative), JS
> (`client/src/lib/pathFinder.js` + `analysis.js`, for the analysis board / bot) and Python
> (`policyNetwork/board.py`, for NN self-play). Rule changes must be applied to all three.

## Design intent

- The game is mostly about gaining a **gold income advantage** by holding more mines, converting
  that into a **material advantage**, and finally **checkmating** (or winning on gold).
- The opening (kings only) looks trivial but is crucial: a small tempo advantage snowballs.

## Board

- Square board, **6×6 up to 16×16** (even sizes). Default lobby setting is 12×12.
- Terrain types:
  - **Grass / Dirt** — normal squares (cosmetic difference only).
  - **Rocks** — impassable; nothing can stand on them; block lines.
  - **Forest (Trees)** — can be occupied, but blocks movement *through* it ("here and no further").
  - **Mine** — behaves like a forest for movement; can be owned.
- **Treasures** sit on squares; they block like a piece and can be captured for gold.
- Generation modes (`MapMode`): **mirrored** (`m`, half generated and mirrored so both sides are
  equal — recommended) or **fully random** (more diverse, less balanced; bidding recommended).
  Roughly 4% rocks, 4% forests, 4% mines, ~2% treasures on mirrored maps. No mines/treasures in
  the middle squares.
- Maps are reproducible from a seed (`MapSeed`).

## Economy

| Item | Value |
| --- | --- |
| Base income | +1 gold at the start of each of your turns |
| Mine income | +3 gold per owned mine, at the start of the owner's turn (`MINE_INCOME = 3`) |
| Treasure capture | +20 gold |
| Black starting gold | 6 (White starts with 0); overridden by bidding |

- A mine becomes yours when you move or place a piece onto it, and stays yours until the
  opponent occupies it.

### Piece costs

| Piece | Cost |
| --- | --- |
| Queen | 75 (72 before 2026-10-26) |
| Rook | 40 |
| Bishop | 32 |
| Knight | 30 |
| Pawn | 26 |

## Moves

On your turn you do exactly one of:

1. **Move a piece**, or
2. **Place (buy) a new piece**.

### Movement

- King, Queen, Rook, Bishop, Knight move as in chess (no castling, no en passant, no promotion).
- **Pawns**: move one square orthogonally (any of the 4 directions) to an empty square; capture
  one square diagonally (any of the 4 directions). Pawns can also capture treasures diagonally.
- Sliding pieces stop **on** a forest/mine/treasure square (they may enter it, but not pass it).
  They cannot enter or pass rocks.
- Normal check rules: you may not leave your own king in check.

### Placement

- Any piece may be placed on an empty square **adjacent to your king**.
- Rooks, bishops and knights may also be placed adjacent to any of your **queens**.
- Pawns may also be placed adjacent to any of your **non-pawn** pieces.
- Not on rocks, not on pieces or treasures. Placing on a mine captures it.
- When in check, a placement is only legal if it resolves the check (blocks it).

**Queen drops (rule change 2026-10-26).** Before, non-pawn pieces could only be placed next to
the king and a queen cost 72. Goal of the change: make attacking viable before you already have
a big advantage, and make queens stronger. Engine self-play (1000 games each, same boards)
showed checkmates rising from 34% to 54% of games, at unchanged colour balance and game length;
see status.md. Old rules remain available in the engine as `Rules::classic()` / `gg --classic`.

## Game end

| Result code | Meaning |
| --- | --- |
| `w+c` / `b+c` | Checkmate |
| `w+s` / `b+s` | Stalemate — **the stalemating side wins** |
| `w+g` / `b+g` | Gold victory |
| `w+t` / `b+t` | Win on time |
| resign / abort | via `ForceEnd` |

- **Gold victory**: reach `150 + 25 * (size - 6) / 2` gold (6×6 → 150, 8×8 → 175, 10×10 → 200,
  12×12 → 225, 16×16 → 275). Only checked for the player **whose turn is starting** (after
  their income), and only if their king is **not in check**. Reaching the threshold on your
  own move (e.g. a treasure) gives the opponent one move to respond (check, mate, …).
  Fixed 2026-10-24 in C#, Rust and Python; previously both players were checked every turn
  and the higher amount won.
- **Stalemate** (intended rule, confirmed by Mart 2026-10-24): if the side to move is not in
  check and has **no legal moves at all** (no piece moves and no legal piece placements), that
  side **loses**.
  - Fixed in C# `Game.cs` `CheckForMate` on 2026-10-24 (it used to require "only a king and
    < 26 gold"). Python `self_play.py` already used the correct rule. The JS client does not
    adjudicate game end.
- **Checkmate**: king in check, no king move, no other piece move, no affordable placement that
  resolves the check.

## Bidding (optional)

Both players secretly bid gold for the right to play White. The highest bidder plays White, and
the winning bid becomes Black's starting gold. Bids may be negative (Black is better on that
board), in which case Black starts in debt. Mainly intended for fully random maps.

## Time controls & ratings

- Time controls: 1+0, 1+1 (Bullet) · 3+0, 3+2, 5+0, 5+3 (Blitz) · 10+5, 15+10 (Rapid) ·
  30+15, 60+30, no timer (Slow).
- Separate Elo per category; K-factor starts at 100, −5 per game, floor 32.
