/**
 * Move tree data structure for variation analysis.
 *
 * Each node represents a board position reached by a specific move.
 * children[0] is always the mainline continuation; children[1..] are variations.
 */
export class MoveNode {
  constructor(san, boardState, parent) {
    this.san        = san;         // notation string (null for root)
    this.boardState = boardState;  // full board state snapshot
    this.parent     = parent;
    this.children   = [];
    this.ply        = parent ? parent.ply + 1 : 0;
    this.isGameMainline = false;   // true for nodes from the original game
  }

  get moveNum()     { return Math.ceil(this.ply / 2); }
  get isWhiteMove() { return this.ply % 2 === 1; }
}

/**
 * Build a tree from an existing game's mainline moves + stateHistory snapshots.
 * All nodes are flagged isGameMainline = true.
 *
 * stateHistory[0] = initial position (before any move)
 * stateHistory[n] = position after n moves
 * moves[n]        = notation of the (n+1)th move
 */
export function buildFromMainline(moves, stateHistory) {
  const root = new MoveNode(null, stateHistory[0], null);
  root.isGameMainline = true;
  let current = root;
  for (let i = 0; i < moves.length; i++) {
    const node = new MoveNode(moves[i], stateHistory[i + 1] ?? null, current);
    node.isGameMainline = true;
    current.children.push(node);
    current = node;
  }
  return root;
}

/**
 * Add a child to parent.
 * If a child with the same san already exists, return it (no duplicate).
 * The new node becomes the first child only if parent has no children yet;
 * otherwise it's appended as a variation (children[1..]).
 */
export function addChild(parent, san, boardState) {
  const existing = parent.children.find(c => c.san === san);
  if (existing) return existing;
  const node = new MoveNode(san, boardState, parent);
  parent.children.push(node);
  return node;
}

/**
 * Walk from a node up to root, collecting the path (root first).
 */
export function getPath(node) {
  const path = [];
  let cur = node;
  while (cur) { path.unshift(cur); cur = cur.parent; }
  return path;
}

/**
 * Find the last node on the mainline (deepest first-child chain from root).
 */
export function lastMainlineNode(root) {
  let cur = root;
  while (cur.children.length > 0) cur = cur.children[0];
  return cur;
}
