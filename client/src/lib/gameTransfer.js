/**
 * Compact hand-off of a finished game's move tree from GamePage to AnalyzePage (via localStorage).
 *
 * Game positions are ~11 KB each as GameStateDto (every square plus the whole move list), which
 * overflowed localStorage for long games. Here the terrain is stored once and each position only
 * keeps its pieces, mine owners, gold and side to move (~0.5 KB).
 */
import { MoveNode } from './moveTree.js';

export const GAME_TRANSFER_KEY = 'analyze_pending_import';
export const GAME_TRANSFER_FORMAT = 'gamepage2';

/** Parse from/to squares from a move like "e2-e4", "e2xe4" or a placement "e4=Q". */
export function parseSanNotation(san) {
  if (!san) return null;
  const lx = c => c.charCodeAt(0) - 97;
  const clean = san.replace(/[+#]$/, '');
  const mv = clean.match(/^([a-z])(\d+)[x-]([a-z])(\d+)$/);
  if (mv) return {
    from: { x: lx(mv[1]), y: parseInt(mv[2]) - 1 },
    to:   { x: lx(mv[3]), y: parseInt(mv[4]) - 1 },
  };
  const pl = clean.match(/^([a-z])(\d+)=/);
  if (pl) return { from: null, to: { x: lx(pl[1]), y: parseInt(pl[2]) - 1 } };
  return null;
}

/** Pack a GamePage move tree (nodes hold GameStateDto-shaped boardStates). */
export function packGameTree(root) {
  const terrain = root.boardState.squares.map(s => [s.x, s.y, s.type]);
  const index = new Map(terrain.map(([x, y], i) => [`${x},${y}`, i]));
  const pack = (node) => {
    const st = node.boardState;
    const p = [], o = [];
    for (const s of st.squares) {
      const i = index.get(`${s.x},${s.y}`);
      if (s.piece) p.push([i, s.piece.type, s.piece.isWhite ? 1 : 0]);
      if (s.mineOwner) o.push([i, s.mineOwner === 'white' ? 1 : 0]);
    }
    return {
      san: node.san,
      s: { p, o, wg: st.white.gold, bg: st.black.gold, wa: st.white.isActive ? 1 : 0 },
      m: node.isGameMainline ? 1 : 0,
      c: node.children.filter(c => c.boardState).map(pack),   // a node without a state can't be shown
    };
  };
  return { terrain, tree: pack(root) };
}

/** Rebuild a MoveNode tree with AnalyzePage snapshots ({ squares, whiteGold, blackGold, whiteIsActive, lastMoveFrom, lastMoveTo }). */
export function unpackGameTree({ terrain, tree }) {
  const build = (n, parent) => {
    const squares = terrain.map(([x, y, type]) => ({ x, y, type, piece: null, mineOwner: null }));
    for (const [i, type, w] of n.s.p) squares[i].piece = { type, isWhite: !!w };
    for (const [i, w] of n.s.o) squares[i].mineOwner = w ? 'white' : 'black';
    const parsed = parseSanNotation(n.san);
    const snapshot = {
      squares,
      whiteGold: n.s.wg,
      blackGold: n.s.bg,
      whiteIsActive: !!n.s.wa,
      lastMoveFrom: parsed?.from ?? null,
      lastMoveTo: parsed?.to ?? null,
    };
    const node = new MoveNode(n.san, snapshot, parent);
    node.isGameMainline = !!n.m;
    node.children = n.c.map(c => build(c, node));
    return node;
  };
  return build(tree, null);
}
