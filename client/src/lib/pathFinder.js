// ── Precomputed tables (cached by board size) ──────────────────────────────
const _neighborCache = new Map();
const _knightCache = new Map();

function getNeighborTable(size) {
  if (_neighborCache.has(size)) return _neighborCache.get(size);
  const n = size * size;
  const table = new Int16Array(n * 8).fill(-1);
  // Direction: 0=N, 1=NE, 2=E, 3=SE, 4=S, 5=SW, 6=W, 7=NW
  const deltas = [
    [0, 1], [1, 1], [1, 0], [1, -1],
    [0, -1], [-1, -1], [-1, 0], [-1, 1],
  ];
  for (let id = 0; id < n; id++) {
    const x = id % size, y = (id / size) | 0;
    for (let d = 0; d < 8; d++) {
      const nx = x + deltas[d][0], ny = y + deltas[d][1];
      if (nx >= 0 && nx < size && ny >= 0 && ny < size) {
        table[id * 8 + d] = ny * size + nx;
      }
    }
  }
  _neighborCache.set(size, table);
  return table;
}

function getKnightTable(size) {
  if (_knightCache.has(size)) return _knightCache.get(size);
  const n = size * size;
  const table = new Int16Array(n * 8).fill(-1);
  const deltas = [
    [1, 2], [2, 1], [2, -1], [1, -2],
    [-1, -2], [-2, -1], [-2, 1], [-1, 2],
  ];
  for (let id = 0; id < n; id++) {
    const x = id % size, y = (id / size) | 0;
    for (let d = 0; d < 8; d++) {
      const nx = x + deltas[d][0], ny = y + deltas[d][1];
      if (nx >= 0 && nx < size && ny >= 0 && ny < size) {
        table[id * 8 + d] = ny * size + nx;
      }
    }
  }
  _knightCache.set(size, table);
  return table;
}

// ── Utility exports (unchanged API) ────────────────────────────────────────

export function isRocks(type) {
  return type === "DirtRocks" || type === "GrassRocks";
}

function isResource(type) {
  return type === "DirtMine" || type === "GrassMine" ||
         type === "DirtTrees" || type === "GrassTrees";
}

export function isRealPiece(p) {
  if (!p) return false;
  return p.type !== "Treasure";
}

export function basePieceType(type) {
  return type.replace(/^(White|Black)/, "");
}

// ── Context: build once, pass to multiple computeLegalMoves calls ──────────

export function buildContext(board) {
  const size = Math.round(Math.sqrt(board.length));
  const lookup = new Array(board.length);
  for (const s of board) lookup[s.y * size + s.x] = s;
  return {
    lookup,
    size,
    neighbors: getNeighborTable(size),
    knights: getKnightTable(size),
  };
}

// ── Blocker type (ported from Python Square.blocker_type) ──────────────────
// 0 = not blocking, 1 = full blocker (can't enter), 2 = can enter but stops
function blockerType(sq, checkingForWhite) {
  if (isRocks(sq.type)) return 1;
  if (isRealPiece(sq.piece) && sq.piece.isWhite === checkingForWhite) return 1;
  if (isResource(sq.type) || sq.piece) return 2;
  return 0;
}

// ── Sliding vector (ported from Python open_squares_vector) ────────────────
function openSquaresVector(sqId, direction, isWhite, ctx) {
  const { lookup, neighbors } = ctx;
  const result = [];
  let curId = sqId;
  while (true) {
    const destId = neighbors[curId * 8 + direction];
    if (destId === -1) return result;
    const dest = lookup[destId];
    const bt = blockerType(dest, isWhite);
    if (bt === 1) return result;
    result.push(destId);
    if (bt === 2) return result;
    curId = destId;
  }
}

// ── Check detection (ported from Python is_capturable / discover_attacker_vector)

function discoverAttackerVector(sqId, direction, defenderIsWhite, ctx) {
  const { lookup, neighbors } = ctx;
  let curId = sqId;
  let dist = 1;
  while (true) {
    const destId = neighbors[curId * 8 + direction];
    if (destId === -1) return false;
    const sq = lookup[destId];
    const bt = blockerType(sq, defenderIsWhite);
    if (bt === 1) return false;
    if (bt === 2) {
      const p = sq.piece;
      if (!p || !isRealPiece(p)) return false;
      const base = basePieceType(p.type);
      if (base === "Knight") return false;
      if (base === "King") return dist === 1;
      if (base === "Pawn") return dist === 1 && direction % 2 === 1;
      if (base === "Queen") return true;
      if (base === "Rook") return direction % 2 === 0;
      if (base === "Bishop") return direction % 2 === 1;
      return false;
    }
    curId = destId;
    dist++;
  }
}

function isCapturable(sqId, capturableByWhite, ctx) {
  const defenderIsWhite = !capturableByWhite;
  for (let d = 0; d < 8; d++) {
    if (discoverAttackerVector(sqId, d, defenderIsWhite, ctx)) return true;
  }
  const { lookup, knights } = ctx;
  for (let d = 0; d < 8; d++) {
    const destId = knights[sqId * 8 + d];
    if (destId === -1) continue;
    const sq = lookup[destId];
    if (isRealPiece(sq.piece) && basePieceType(sq.piece.type) === "Knight" &&
        sq.piece.isWhite === capturableByWhite) {
      return true;
    }
  }
  return false;
}

// ── Test move for king safety (ported from Python test_move) ───────────────
function testMove(sourceId, destId, isWhite, ctx) {
  const { lookup } = ctx;
  const srcSq = lookup[sourceId];
  const dstSq = lookup[destId];

  const savedSrcPiece = srcSq.piece;
  const savedDstPiece = dstSq.piece;

  dstSq.piece = srcSq.piece;
  srcSq.piece = null;

  const isKingMove = isRealPiece(dstSq.piece) && basePieceType(dstSq.piece.type) === "King";
  let kingId;
  if (isKingMove) {
    kingId = destId;
  } else {
    for (let i = 0; i < lookup.length; i++) {
      const sq = lookup[i];
      if (isRealPiece(sq.piece) && sq.piece.isWhite === isWhite &&
          basePieceType(sq.piece.type) === "King") {
        kingId = i;
        break;
      }
    }
  }

  const inCheck = isCapturable(kingId, !isWhite, ctx);

  srcSq.piece = savedSrcPiece;
  dstSq.piece = savedDstPiece;

  return !inCheck;
}

// ── Legal move generation per piece ────────────────────────────────────────

function legalKingMoves(sqId, isWhite, ctx) {
  const { lookup, neighbors } = ctx;
  const moves = [];
  for (let d = 0; d < 8; d++) {
    const destId = neighbors[sqId * 8 + d];
    if (destId === -1) continue;
    const dest = lookup[destId];
    if (isRocks(dest.type)) continue;
    if (isRealPiece(dest.piece) && dest.piece.isWhite === isWhite) continue;
    if (testMove(sqId, destId, isWhite, ctx)) {
      moves.push(destId);
    }
  }
  return moves;
}

function legalSlidingMoves(sqId, isWhite, directions, ctx) {
  const moves = [];
  for (const dir of directions) {
    for (const destId of openSquaresVector(sqId, dir, isWhite, ctx)) {
      if (testMove(sqId, destId, isWhite, ctx)) {
        moves.push(destId);
      }
    }
  }
  return moves;
}

function legalKnightMoves(sqId, isWhite, ctx) {
  const { lookup, knights } = ctx;
  const moves = [];
  for (let d = 0; d < 8; d++) {
    const destId = knights[sqId * 8 + d];
    if (destId === -1) continue;
    const dest = lookup[destId];
    if (isRocks(dest.type)) continue;
    if (isRealPiece(dest.piece) && dest.piece.isWhite === isWhite) continue;
    if (testMove(sqId, destId, isWhite, ctx)) {
      moves.push(destId);
    }
  }
  return moves;
}

function legalPawnMoves(sqId, isWhite, ctx) {
  const { lookup, neighbors } = ctx;
  const moves = [];
  for (let d = 0; d < 8; d++) {
    const destId = neighbors[sqId * 8 + d];
    if (destId === -1) continue;
    const dest = lookup[destId];
    if (isRocks(dest.type)) continue;
    if (isRealPiece(dest.piece) && dest.piece.isWhite === isWhite) continue;
    if (d % 2 === 0) {
      // Orthogonal: only empty squares (no pieces, no treasures)
      if (dest.piece) continue;
    } else {
      // Diagonal: only if enemy piece or treasure
      if (!dest.piece) continue;
    }
    if (testMove(sqId, destId, isWhite, ctx)) {
      moves.push(destId);
    }
  }
  return moves;
}

// ── Main export ────────────────────────────────────────────────────────────

export function computeLegalMoves(square, board, ctx) {
  if (!square?.piece || !isRealPiece(square.piece)) return [];

  if (!ctx) ctx = buildContext(board);
  const { size } = ctx;
  const piece = square.piece;
  const isWhite = piece.isWhite;
  const sqId = square.y * size + square.x;
  const baseType = basePieceType(piece.type);

  let destIds;
  if (baseType === "King") {
    destIds = legalKingMoves(sqId, isWhite, ctx);
  } else if (baseType === "Queen") {
    destIds = legalSlidingMoves(sqId, isWhite, [0,1,2,3,4,5,6,7], ctx);
  } else if (baseType === "Rook") {
    destIds = legalSlidingMoves(sqId, isWhite, [0,2,4,6], ctx);
  } else if (baseType === "Bishop") {
    destIds = legalSlidingMoves(sqId, isWhite, [1,3,5,7], ctx);
  } else if (baseType === "Knight") {
    destIds = legalKnightMoves(sqId, isWhite, ctx);
  } else if (baseType === "Pawn") {
    destIds = legalPawnMoves(sqId, isWhite, ctx);
  } else {
    return [];
  }

  return destIds.map(id => ({ x: id % size, y: (id / size) | 0 }));
}
