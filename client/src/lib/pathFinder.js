const DIRS = {
  N: [0, -1],
  NE: [1, -1],
  E: [1, 0],
  SE: [1, 1],
  S: [0, 1],
  SW: [-1, 1],
  W: [-1, 0],
  NW: [-1, -1],
};
const CARDINAL = ["N", "E", "S", "W"];
const DIAGONAL = ["NE", "SE", "SW", "NW"];
const ALL8 = [...CARDINAL, ...DIAGONAL];

export function isRocks(type) {
  return type === "DirtRocks" || type === "GrassRocks";
}
function isResource(type) {
  return (
    type === "DirtMine" ||
    type === "GrassMine" ||
    type === "DirtTrees" ||
    type === "GrassTrees"
  );
}
// A "real piece" is a moveable chess piece — NOT a Treasure or WhiteFlag (GaiaObjects)
export function isRealPiece(p) {
  if (!p) return false;
  const t = p.type;
  return t !== "Treasure";
}
export function basePieceType(type) {
  return type.replace(/^(White|Black)/, "");
}

/**
 * Port of PathFinder.FindLegalSquaresVector.
 * Returns SquareDtos that are reachable in this direction.
 * maxSteps = null → unlimited (sliding piece)
 */
function legalVector(piece, sq, dx, dy, maxSteps, lookup) {
  const legal = [];
  let cx = sq.x + dx,
    cy = sq.y + dy,
    steps = 0;

  while (maxSteps === null || steps < maxSteps) {
    const cur = lookup.get(`${cx},${cy}`);
    if (!cur) break; // off board

    if (isRocks(cur.type)) break; // rocks block everything

    if (isResource(cur.type)) {
      // Allied real piece on a resource square → stop (don't add)
      if (isRealPiece(cur.piece) && cur.piece.isWhite === piece.isWhite) break;
      // Otherwise (empty, treasure, or enemy) → add and stop
      legal.push(cur);
      break;
    }

    if (cur.piece) {
      if (isRealPiece(cur.piece)) {
        if (cur.piece.isWhite === piece.isWhite) break; // friendly → stop
      }
      // Enemy piece or GaiaObject (Treasure etc.) → add and stop
      legal.push(cur);
      break;
    }

    // Empty passable square
    legal.push(cur);
    steps++;
    cx += dx;
    cy += dy;
  }
  return legal;
}

export function computeLegalMoves(square, board) {
  if (!square?.piece || !isRealPiece(square.piece)) return [];
  const piece = square.piece;
  const baseType = basePieceType(piece.type);
  const lookup = new Map();
  for (const s of board) lookup.set(`${s.x},${s.y}`, s);

  const legal = [];

  function slide(dirs, maxSteps = null) {
    for (const dir of dirs) {
      const [dx, dy] = DIRS[dir];
      legal.push(...legalVector(piece, square, dx, dy, maxSteps, lookup));
    }
  }

  if (baseType === "Pawn") {
    // Orthogonal moves: only empty squares (no captures, not even treasures)
    for (const dir of CARDINAL) {
      const [dx, dy] = DIRS[dir];
      const moves = legalVector(piece, square, dx, dy, 1, lookup);
      legal.push(...moves.filter((s) => !s.piece));
    }
    // Diagonal captures: enemy real pieces or GaiaObjects (treasures/flags)
    for (const dir of DIAGONAL) {
      const [dx, dy] = DIRS[dir];
      const moves = legalVector(piece, square, dx, dy, 1, lookup);
      legal.push(
        ...moves.filter(
          (s) =>
            s.piece &&
            !(isRealPiece(s.piece) && s.piece.isWhite === piece.isWhite),
        ),
      );
    }
  } else if (baseType === "Knight") {
    const jumps = [
      [1, 2],
      [1, -2],
      [-1, 2],
      [-1, -2],
      [2, 1],
      [2, -1],
      [-2, 1],
      [-2, -1],
    ];
    for (const [dx, dy] of jumps) {
      const dest = lookup.get(`${square.x + dx},${square.y + dy}`);
      if (!dest) continue;
      if (isRocks(dest.type)) continue;
      if (isRealPiece(dest.piece) && dest.piece.isWhite === piece.isWhite)
        continue;
      legal.push(dest);
    }
  } else if (baseType === "King") {
    slide(ALL8, 1);
  } else if (baseType === "Rook") {
    slide(CARDINAL);
  } else if (baseType === "Bishop") {
    slide(DIAGONAL);
  } else if (baseType === "Queen") {
    slide(ALL8);
  }

  return legal.map((s) => ({ x: s.x, y: s.y }));
}
