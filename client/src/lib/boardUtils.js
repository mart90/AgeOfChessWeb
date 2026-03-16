/**
 * Client-side board state utilities for variation analysis.
 * Works with GameStateDto-shaped state objects.
 *
 * State shape:
 *   squares: Array<{ x, y, id, type, piece: {type, isWhite}|null, mineOwner: string|null, ... }>
 *   white:   { gold, isActive, ... }
 *   black:   { gold, isActive, ... }
 *   moves:   string[]   (existing move list; we append the new san)
 */
import { MINE_INCOME } from './constants.js';

function col(x) { return String.fromCharCode(97 + x); }

/**
 * Returns notation string for a regular piece move.
 */
export function moveToNotation(fromX, fromY, toX, toY, isCapture) {
  return `${col(fromX)}${fromY + 1}${isCapture ? 'x' : '-'}${col(toX)}${toY + 1}`;
}

const PIECE_CODES = { Pawn: 'p', Queen: 'Q', Rook: 'R', Bishop: 'B', Knight: 'N' };

/**
 * Returns notation string for a piece placement.
 */
export function placementToNotation(x, y, pieceType) {
  return `${col(x)}${y + 1}=${PIECE_CODES[pieceType] ?? pieceType[0].toUpperCase()}`;
}

/**
 * Apply a piece placement to a state and return a new state.
 * pieceType: 'Queen' | 'Rook' | 'Bishop' | 'Knight' | 'Pawn'
 * cost: gold cost deducted from the active player
 */
export function applyPlacementToState(state, x, y, pieceType, cost) {
  const isWhitePlacing = state.white.isActive;
  let wg = state.white.gold;
  let bg = state.black.gold;
  if (isWhitePlacing) wg -= cost; else bg -= cost;

  const newSquares = state.squares.map(s => {
    if (s.x === x && s.y === y) {
      const capturedMine = s.type?.includes('Mine');
      return {
        ...s,
        piece: { type: (isWhitePlacing ? 'White' : 'Black') + pieceType, isWhite: isWhitePlacing },
        mineOwner: capturedMine ? (isWhitePlacing ? 'white' : 'black') : s.mineOwner,
      };
    }
    return s;
  });

  const nextWhite = !isWhitePlacing;

  // Mine income for the newly active player
  const income = newSquares
    .filter(s => s.type?.includes('Mine') && s.mineOwner === (nextWhite ? 'white' : 'black'))
    .length * MINE_INCOME;
  if (nextWhite) wg += income; else bg += income;

  // Base turn income (+1)
  if (nextWhite) wg += 1; else bg += 1;

  const san = placementToNotation(x, y, pieceType);

  return {
    ...state,
    squares: newSquares,
    white: { ...state.white, gold: wg, isActive: nextWhite },
    black: { ...state.black, gold: bg, isActive: !nextWhite },
    moves: [...(state.moves ?? []), san],
  };
}

/**
 * Apply a move to a state (GameStateDto-shape) and return a new state.
 * Does NOT validate legality — call computeLegalMoves first if needed.
 */
export function applyMoveToState(state, fromX, fromY, toX, toY) {
  const fromSq = state.squares.find(s => s.x === fromX && s.y === fromY);
  if (!fromSq?.piece) return state;

  const piece     = fromSq.piece;
  const isCapture = state.squares.some(s => s.x === toX && s.y === toY && s.piece != null);
  let wg = state.white.gold;
  let bg = state.black.gold;

  // Treasure capture
  const toSq = state.squares.find(s => s.x === toX && s.y === toY);
  if (toSq?.piece?.type === 'Treasure') {
    if (piece.isWhite) wg += 20; else bg += 20;
  }

  const newSquares = state.squares.map(s => {
    if (s.x === fromX && s.y === fromY) return { ...s, piece: null };
    if (s.x === toX   && s.y === toY) {
      const capturedMine = s.type?.includes('Mine');
      return {
        ...s,
        piece: piece,
        mineOwner: capturedMine ? (piece.isWhite ? 'white' : 'black') : s.mineOwner,
      };
    }
    return s;
  });

  const nextWhite = !piece.isWhite;

  // Mine income for the newly active player
  const income = newSquares
    .filter(s => s.type?.includes('Mine') && s.mineOwner === (nextWhite ? 'white' : 'black'))
    .length * MINE_INCOME;
  if (nextWhite) wg += income; else bg += income;

  // Base turn income (+1)
  if (nextWhite) wg += 1; else bg += 1;

  const san = moveToNotation(fromX, fromY, toX, toY, isCapture);

  return {
    ...state,
    squares: newSquares,
    white: { ...state.white, gold: wg, isActive: nextWhite },
    black: { ...state.black, gold: bg, isActive: !nextWhite },
    moves: [...(state.moves ?? []), san],
  };
}
