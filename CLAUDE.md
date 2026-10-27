# AgeOfChessWeb (Goldrush Gambit)

Read these first, and keep them up to date as work progresses:

- [docs/goals.md](docs/goals.md) — what the project is and what we're aiming for
- [docs/status.md](docs/status.md) — what we're currently working on and its status
- [docs/game-rules.md](docs/game-rules.md) — rules and design of Goldrush Gambit
- [docs/engine.md](docs/engine.md) — the Rust engine (`engine/`), rules oracle, benchmarks
- [docs/training.md](docs/training.md) — evaluation-network training (`training/`), data format, logging conventions

## Conventions

- Authoritative game logic: `AgeOfChess.Server/GameLogic/`. Rules are duplicated in Rust
  (`engine/`, checked against C# by `tools/RulesOracle`) and JS (`client/src/lib/`) — keep all
  three in sync. Training code (`training/`) has no rules of its own; it reads engine output.
- Never edit the old MonoGame repo at `C:\Users\Mart\source\repos\AgeOfChess`.
- Build server: `dotnet build AgeOfChess.Server/AgeOfChess.Server.csproj` (a locked exe from a
  running server is not a compile error). Build client: `cd client && npm run build`.
- Verify builds after changes. Don't commit unless asked.
- Mart prefers concise responses.
