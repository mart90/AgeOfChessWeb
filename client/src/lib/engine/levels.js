// Computer opponent levels: fixed node budget per move, so strength is the same on every
// device. Elo values are estimates (see docs/engine.md). Level 1 makes deliberate mistakes
// (the server sends computerMistakeRate/computerMistakeMaxLoss with the game state; see GamePage).
// Keep in sync with AgeOfChess.Server/Services/ComputerLevels.cs.
export const COMPUTER_LEVELS = [
  { level: 1, nodes:   1_000, elo:  800 },
  { level: 2, nodes:   1_000, elo: 1000 },
  { level: 3, nodes:   2_400, elo: 1200 },
  { level: 4, nodes:   6_000, elo: 1400 },
  { level: 5, nodes:  12_000, elo: 1600 },
  { level: 6, nodes:  60_000, elo: 1800 },
  { level: 7, nodes: 600_000, elo: 2000 },
];
