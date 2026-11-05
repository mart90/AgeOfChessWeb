// Replays games recorded by tools/RulesOracle (the server's C# GameLogic) and checks that the JS
// rules (src/lib/pathFinder.js) produce the same legal moves and placements in every position.
// Usage: node scripts/rules-oracle.mjs [file.jsonl]   (default: ../engine/testdata/oracle.jsonl,
// see docs/engine.md for how to generate it)
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { computeLegalMoves, computeLegalPlacements, buildContext } from '../src/lib/pathFinder.js';

const file = process.argv[2] ?? fileURLToPath(new URL('../../engine/testdata/oracle.jsonl', import.meta.url));
const PLACE = { q: 'Queen', r: 'Rook', b: 'Bishop', n: 'Knight', p: 'Pawn' };
const COST = { q: 75, r: 40, b: 32, n: 30, p: 26 };

// Board from a map seed, as MapGenerator.GenerateFromSeed (see engine/src/seed.rs)
function boardFromSeed(seed) {
  const [mode, dims, body] = seed.split('_');
  const size = Number(dims.split('x')[0]);
  const n = size * size;
  const board = Array.from({ length: n }, (_, id) => ({ x: id % size, y: (id / size) | 0, type: 'Grass', piece: null }));
  const terrain = { m: 'GrassMine', r: 'GrassRocks', f: 'GrassTrees' };
  let id = 0;
  for (const c of body) {
    if (c >= '0' && c <= '9') { id += Number(c); continue; }
    if (c === 'k') board[id].piece = { type: id >= n / 2 ? 'BlackKing' : 'WhiteKing', isWhite: id < n / 2 };
    else if (c === 't') board[id].piece = { type: 'Treasure', isWhite: false };
    else board[id].type = terrain[c];
    id++;
  }
  if (mode === 'm') {
    for (let i = 0; i < n / 2 - 1; i++) {
      const j = n - 1 - i;
      board[j].type = board[i].type;
      const p = board[i].piece;
      if (p?.type === 'Treasure') board[j].piece = { type: 'Treasure', isWhite: false };
      else if (p?.type === 'WhiteKing') board[j].piece = { type: 'BlackKing', isWhite: false };
    }
  }
  return board;
}

function legalCodes(board, isWhite, gold) {
  const ctx = buildContext(board);
  const id = s => s.y * ctx.size + s.x;
  const codes = [];
  for (const sq of board) {
    if (sq.piece && sq.piece.type !== 'Treasure' && sq.piece.isWhite === isWhite)
      for (const d of computeLegalMoves(sq, null, ctx)) codes.push(`m${id(sq)}-${id(d)}`);
  }
  for (const [c, type] of Object.entries(PLACE)) {
    if (gold < COST[c]) continue;
    for (const d of computeLegalPlacements(null, isWhite, type, ctx)) codes.push(`p${c}${id(d)}`);
  }
  return codes.sort();
}

let games = 0, positions = 0, failures = 0;
for (const line of readFileSync(file, 'utf8').split('\n')) {
  if (!line.trim()) continue;
  const game = JSON.parse(line);
  if (!game.queenDrops) continue; // recorded under the old placement rules
  games++;
  const board = boardFromSeed(game.seed);
  let white = true;
  for (const [pi, ply] of game.plies.entries()) {
    positions++;
    const ours = legalCodes(board, white, white ? ply.gold[0] : ply.gold[1]);
    const theirs = [...ply.legal].sort();
    if (ours.join() !== theirs.join()) {
      failures++;
      console.log(`game ${games - 1} (${game.seed}) ply ${pi}: missing ${theirs.filter(c => !ours.includes(c))}; extra ${ours.filter(c => !theirs.includes(c))}`);
      break;
    }
    if (!ply.move) break;
    const m = ply.move;
    if (m[0] === 'm') {
      const [from, to] = m.slice(1).split('-').map(Number);
      board[to].piece = board[from].piece;
      board[from].piece = null;
    } else {
      board[Number(m.slice(2))].piece = { type: (white ? 'White' : 'Black') + PLACE[m[1]], isWhite: white };
    }
    white = !white;
  }
}
console.log(`${games} games, ${positions} positions, ${failures} mismatches`);
process.exit(failures || !games ? 1 : 0);
