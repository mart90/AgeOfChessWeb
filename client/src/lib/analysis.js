import { computeLegalMoves, buildContext } from "./pathFinder";
import { SHOP, MINE_INCOME } from './constants'

const MAX_DEPTH = 10;

export let stopRequested = false;
let nodeCount = 0;
const YIELD_INTERVAL = 1000;

const transpositionTable = new Map();
const MAX_TT_SIZE = 500000;

export function requestStop() {
    stopRequested = true;
}

export function resetStop() {
    stopRequested = false;
    nodeCount = 0;
}

export function clearTranspositionTable() {
    transpositionTable.clear();
}

// ── Piece values for evaluation ──────────────────────────────────────────────

const PIECE_VALUES = {
    'WhiteQueen': 90, 'BlackQueen': 90,
    'WhiteRook': 52, 'BlackRook': 52,
    'WhiteBishop': 42, 'BlackBishop': 42,
    'WhiteKnight': 40, 'BlackKnight': 40,
    'WhitePawn': 25, 'BlackPawn': 25
};

function pieceValue(pieceType) {
    return PIECE_VALUES[pieceType] || 0;
}

// ── Zobrist hashing ──────────────────────────────────────────────────────────

const PIECE_INDEX = {
    'WhiteKing': 0, 'WhiteQueen': 1, 'WhiteRook': 2, 'WhiteBishop': 3, 'WhiteKnight': 4, 'WhitePawn': 5,
    'BlackKing': 6, 'BlackQueen': 7, 'BlackRook': 8, 'BlackBishop': 9, 'BlackKnight': 10, 'BlackPawn': 11,
    'Treasure': 12
};
const NUM_PIECE_TYPES = 13;
const NUM_MINE_STATES = 3; // 0=none, 1=white, 2=black

function mineOwnerIdx(owner) {
    if (!owner) return 0;
    return owner === 'white' ? 1 : 2;
}

// Deterministic PRNG (mulberry32)
function mulberry32(seed) {
    return function () {
        seed |= 0;
        seed = (seed + 0x6D2B79F5) | 0;
        let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
        t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
        return ((t ^ (t >>> 14)) >>> 0);
    };
}

const _zobristCache = new Map();

function getZobristKeys(size) {
    if (_zobristCache.has(size)) return _zobristCache.get(size);

    const n = size * size;
    const rand = mulberry32(0x12345678 + size);

    const pieceKeysHi = new Uint32Array(n * NUM_PIECE_TYPES);
    const pieceKeysLo = new Uint32Array(n * NUM_PIECE_TYPES);
    for (let i = 0; i < n * NUM_PIECE_TYPES; i++) {
        pieceKeysHi[i] = rand();
        pieceKeysLo[i] = rand();
    }

    const mineKeysHi = new Uint32Array(n * NUM_MINE_STATES);
    const mineKeysLo = new Uint32Array(n * NUM_MINE_STATES);
    for (let i = 0; i < n * NUM_MINE_STATES; i++) {
        mineKeysHi[i] = rand();
        mineKeysLo[i] = rand();
    }

    const sideHi = rand();
    const sideLo = rand();

    const keys = { pieceKeysHi, pieceKeysLo, mineKeysHi, mineKeysLo, sideHi, sideLo };
    _zobristCache.set(size, keys);
    return keys;
}

function computeFullHash(lookup, whiteIsActive, keys) {
    let hi = 0, lo = 0;

    for (let i = 0; i < lookup.length; i++) {
        const sq = lookup[i];
        if (sq.piece) {
            const idx = PIECE_INDEX[sq.piece.type];
            if (idx !== undefined) {
                const k = i * NUM_PIECE_TYPES + idx;
                hi ^= keys.pieceKeysHi[k];
                lo ^= keys.pieceKeysLo[k];
            }
        }
        if (sq.type && sq.type.includes('Mine')) {
            const mi = mineOwnerIdx(sq.mineOwner);
            const k = i * NUM_MINE_STATES + mi;
            hi ^= keys.mineKeysHi[k];
            lo ^= keys.mineKeysLo[k];
        }
    }

    if (whiteIsActive) {
        hi ^= keys.sideHi;
        lo ^= keys.sideLo;
    }

    return { hi, lo };
}

// ── Board state ──────────────────────────────────────────────────────────────

function createBoardState(position, whiteGold, blackGold, whiteIsActive) {
    const ctx = buildContext(position);
    const { lookup, size } = ctx;

    let whiteMines = 0, blackMines = 0;
    let whiteKingId = -1, blackKingId = -1;

    for (let i = 0; i < lookup.length; i++) {
        const sq = lookup[i];
        if (sq.type && sq.type.includes('Mine')) {
            if (sq.mineOwner === 'white') whiteMines++;
            else if (sq.mineOwner === 'black') blackMines++;
        }
        if (sq.piece) {
            if (sq.piece.type === 'WhiteKing') whiteKingId = i;
            else if (sq.piece.type === 'BlackKing') blackKingId = i;
        }
    }

    const keys = getZobristKeys(size);
    const { hi, lo } = computeFullHash(lookup, whiteIsActive, keys);

    return {
        ctx,
        keys,
        whiteGold,
        blackGold,
        whiteIsActive,
        whiteMines,
        blackMines,
        whiteKingId,
        blackKingId,
        hashHi: hi,
        hashLo: lo
    };
}

// ── Make / unmake ────────────────────────────────────────────────────────────

function makeMove(state, move) {
    const { ctx, keys } = state;
    const { lookup, size } = ctx;

    const undo = {
        prevWhiteGold: state.whiteGold,
        prevBlackGold: state.blackGold,
        prevHashHi: state.hashHi,
        prevHashLo: state.hashLo,
        prevWhiteMines: state.whiteMines,
        prevBlackMines: state.blackMines,
        prevWhiteKingId: state.whiteKingId,
        prevBlackKingId: state.blackKingId,
    };

    if (move.type === "m") {
        const fromId = move.fromPos.y * size + move.fromPos.x;
        const toId = move.toPos.y * size + move.toPos.x;
        const fromSq = lookup[fromId];
        const toSq = lookup[toId];

        undo.moveType = "m";
        undo.fromId = fromId;
        undo.toId = toId;
        undo.fromPiece = fromSq.piece;
        undo.toPiece = toSq.piece;
        undo.toMineOwner = toSq.mineOwner;

        const movingPiece = fromSq.piece;
        const movingIdx = PIECE_INDEX[movingPiece.type];

        // XOR out piece at source
        let k = fromId * NUM_PIECE_TYPES + movingIdx;
        state.hashHi ^= keys.pieceKeysHi[k];
        state.hashLo ^= keys.pieceKeysLo[k];

        // Handle capture
        if (toSq.piece) {
            const capIdx = PIECE_INDEX[toSq.piece.type];
            if (capIdx !== undefined) {
                k = toId * NUM_PIECE_TYPES + capIdx;
                state.hashHi ^= keys.pieceKeysHi[k];
                state.hashLo ^= keys.pieceKeysLo[k];
            }
            if (toSq.piece.type === "Treasure") {
                if (movingPiece.isWhite) state.whiteGold += 20;
                else state.blackGold += 20;
            }
        }

        // XOR in piece at destination
        k = toId * NUM_PIECE_TYPES + movingIdx;
        state.hashHi ^= keys.pieceKeysHi[k];
        state.hashLo ^= keys.pieceKeysLo[k];

        // Move piece
        toSq.piece = movingPiece;
        fromSq.piece = null;

        // King tracking
        if (movingPiece.type === 'WhiteKing') state.whiteKingId = toId;
        else if (movingPiece.type === 'BlackKing') state.blackKingId = toId;

        // Mine capture
        if (toSq.type && toSq.type.includes('Mine')) {
            const oldOwner = undo.toMineOwner;
            const newOwner = movingPiece.isWhite ? 'white' : 'black';
            if (oldOwner !== newOwner) {
                // XOR out old mine state
                const oldMi = mineOwnerIdx(oldOwner);
                k = toId * NUM_MINE_STATES + oldMi;
                state.hashHi ^= keys.mineKeysHi[k];
                state.hashLo ^= keys.mineKeysLo[k];
                // XOR in new mine state
                const newMi = mineOwnerIdx(newOwner);
                k = toId * NUM_MINE_STATES + newMi;
                state.hashHi ^= keys.mineKeysHi[k];
                state.hashLo ^= keys.mineKeysLo[k];
                // Update counts
                if (oldOwner === 'white') state.whiteMines--;
                else if (oldOwner === 'black') state.blackMines--;
                if (newOwner === 'white') state.whiteMines++;
                else state.blackMines++;
                toSq.mineOwner = newOwner;
            }
        }
    } else {
        // Placement
        const toId = move.toPos.y * size + move.toPos.x;
        const toSq = lookup[toId];

        const pieceType = (state.whiteIsActive ? "White" : "Black") + move.pieceType;
        const piece = { type: pieceType, isWhite: state.whiteIsActive };

        undo.moveType = "p";
        undo.toId = toId;
        undo.toPiece = toSq.piece;
        undo.toMineOwner = toSq.mineOwner;

        // Deduct cost
        if (state.whiteIsActive) state.whiteGold -= move.cost;
        else state.blackGold -= move.cost;

        // XOR in piece at dest
        const idx = PIECE_INDEX[pieceType];
        let k = toId * NUM_PIECE_TYPES + idx;
        state.hashHi ^= keys.pieceKeysHi[k];
        state.hashLo ^= keys.pieceKeysLo[k];

        toSq.piece = piece;

        // Mine capture
        if (toSq.type && toSq.type.includes('Mine')) {
            const oldOwner = undo.toMineOwner;
            const newOwner = state.whiteIsActive ? 'white' : 'black';
            if (oldOwner !== newOwner) {
                const oldMi = mineOwnerIdx(oldOwner);
                k = toId * NUM_MINE_STATES + oldMi;
                state.hashHi ^= keys.mineKeysHi[k];
                state.hashLo ^= keys.mineKeysLo[k];
                const newMi = mineOwnerIdx(newOwner);
                k = toId * NUM_MINE_STATES + newMi;
                state.hashHi ^= keys.mineKeysHi[k];
                state.hashLo ^= keys.mineKeysLo[k];
                if (oldOwner === 'white') state.whiteMines--;
                else if (oldOwner === 'black') state.blackMines--;
                if (newOwner === 'white') state.whiteMines++;
                else state.blackMines++;
                toSq.mineOwner = newOwner;
            }
        }
    }

    // Switch sides
    state.whiteIsActive = !state.whiteIsActive;
    state.hashHi ^= keys.sideHi;
    state.hashLo ^= keys.sideLo;

    // Income for newly active player (1 base + mines × MINE_INCOME)
    const mines = state.whiteIsActive ? state.whiteMines : state.blackMines;
    const income = 1 + mines * MINE_INCOME;
    if (state.whiteIsActive) state.whiteGold += income;
    else state.blackGold += income;

    return undo;
}

function unmakeMove(state, undo) {
    const { ctx } = state;
    const { lookup } = ctx;

    if (undo.moveType === "m") {
        lookup[undo.fromId].piece = undo.fromPiece;
        lookup[undo.toId].piece = undo.toPiece;
        lookup[undo.toId].mineOwner = undo.toMineOwner;
    } else {
        lookup[undo.toId].piece = undo.toPiece;
        lookup[undo.toId].mineOwner = undo.toMineOwner;
    }

    state.whiteGold = undo.prevWhiteGold;
    state.blackGold = undo.prevBlackGold;
    state.whiteIsActive = !state.whiteIsActive;
    state.hashHi = undo.prevHashHi;
    state.hashLo = undo.prevHashLo;
    state.whiteMines = undo.prevWhiteMines;
    state.blackMines = undo.prevBlackMines;
    state.whiteKingId = undo.prevWhiteKingId;
    state.blackKingId = undo.prevBlackKingId;
}

// ── Legal move generation ────────────────────────────────────────────────────

const MAJOR_PIECES = new Set([
    'WhiteQueen', 'BlackQueen',
    'WhiteRook', 'BlackRook',
    'WhiteBishop', 'BlackBishop',
    'WhiteKnight', 'BlackKnight'
]);

const VALID_PLACEMENT_TYPES = new Set([
    'Grass', 'GrassTrees', 'GrassMine',
    'Dirt', 'DirtTrees', 'DirtMine'
]);

function getAllLegalNewPiecePlacements(state) {
    const { ctx, whiteIsActive } = state;
    const gold = whiteIsActive ? state.whiteGold : state.blackGold;
    const { lookup, neighbors } = ctx;
    const placements = [];

    const kingId = whiteIsActive ? state.whiteKingId : state.blackKingId;
    if (kingId === -1) return placements;

    // Collect major piece square IDs for pawn placement
    const majorPieceIds = [];
    for (let i = 0; i < lookup.length; i++) {
        const sq = lookup[i];
        if (sq.piece && sq.piece.isWhite === whiteIsActive && MAJOR_PIECES.has(sq.piece.type)) {
            majorPieceIds.push(i);
        }
    }

    for (const shopItem of SHOP) {
        if (gold < shopItem.cost) continue;

        const pieceType = shopItem.type;
        const visited = new Set();

        // Adjacent to king
        for (let d = 0; d < 8; d++) {
            const destId = neighbors[kingId * 8 + d];
            if (destId === -1) continue;
            const dest = lookup[destId];
            if (dest.piece != null || !VALID_PLACEMENT_TYPES.has(dest.type)) continue;
            placements.push({ type: "p", toPos: { x: dest.x, y: dest.y }, pieceType, cost: shopItem.cost });
            visited.add(destId);
        }

        // Pawns can also be placed adjacent to major pieces
        if (pieceType === "Pawn") {
            for (const psId of majorPieceIds) {
                for (let d = 0; d < 8; d++) {
                    const destId = neighbors[psId * 8 + d];
                    if (destId === -1 || visited.has(destId)) continue;
                    const dest = lookup[destId];
                    if (dest.piece != null || !VALID_PLACEMENT_TYPES.has(dest.type)) continue;
                    placements.push({ type: "p", toPos: { x: dest.x, y: dest.y }, pieceType: "Pawn", cost: shopItem.cost });
                    visited.add(destId);
                }
            }
        }
    }

    return placements;
}

function getAllLegalMoves(state) {
    const { ctx, whiteIsActive } = state;
    const { lookup } = ctx;
    const moves = [];

    for (let i = 0; i < lookup.length; i++) {
        const sq = lookup[i];
        if (sq.piece && sq.piece.isWhite === whiteIsActive && sq.piece.type !== 'Treasure') {
            const pieceMoves = computeLegalMoves(sq, null, ctx);
            for (const dest of pieceMoves) {
                moves.push({
                    type: "m",
                    fromPos: { x: sq.x, y: sq.y },
                    toPos: dest
                });
            }
        }
    }

    const gold = whiteIsActive ? state.whiteGold : state.blackGold;
    if (gold >= 20) {
        const placements = getAllLegalNewPiecePlacements(state);
        for (const p of placements) moves.push(p);
    }

    return moves;
}

// ── Evaluation ───────────────────────────────────────────────────────────────

function evaluate(state) {
    const { ctx, whiteGold, blackGold, whiteMines, blackMines } = state;
    const { lookup } = ctx;
    let score = 0;
    let whiteMaterialValue = 0;
    let blackMaterialValue = 0;
    let whiteHasKing = false;
    let blackHasKing = false;

    for (let i = 0; i < lookup.length; i++) {
        const sq = lookup[i];
        if (sq.piece != null) {
            const pt = sq.piece.type;
            if (pt === "WhiteKing") { whiteHasKing = true; continue; }
            if (pt === "BlackKing") { blackHasKing = true; continue; }
            if (pt === "Treasure") continue;
            if (sq.piece.isWhite) {
                whiteMaterialValue += pieceValue(pt) * 1.2;
            } else {
                blackMaterialValue += pieceValue(pt) * 1.2;
            }
        }
    }

    if (!whiteHasKing) score -= 999;
    if (!blackHasKing) score += 999;

    score += (whiteMaterialValue + whiteGold) / 5;
    score -= (blackMaterialValue + blackGold) / 5;

    score += whiteMines * (MINE_INCOME / 2);
    score -= blackMines * (MINE_INCOME / 2);

    return score;
}

// ── Search ───────────────────────────────────────────────────────────────────

async function negamax(state, depth, alpha, beta) {
    if (stopRequested) return 0;

    nodeCount++;
    if (nodeCount % YIELD_INTERVAL === 0) {
        await new Promise(r => setTimeout(r, 0));
    }

    // Transposition table lookup
    const ttKey = `${state.hashHi}:${state.hashLo}:${state.whiteGold}:${state.blackGold}`;
    const ttEntry = transpositionTable.get(ttKey);
    if (ttEntry && ttEntry.depth >= depth) {
        return ttEntry.score;
    }

    const evalScore = evaluate(state);

    if (depth === 0 || Math.abs(evalScore) > 900) {
        return state.whiteIsActive ? evalScore : -evalScore;
    }

    const legalMoves = getAllLegalMoves(state);

    if (legalMoves.length === 0) {
        return state.whiteIsActive ? evalScore : -evalScore;
    }

    let bestScore = -Infinity;
    const FULL_DEPTH_MOVES = 4;
    const REDUCTION_LIMIT = 3;

    for (let i = 0; i < legalMoves.length; i++) {
        const move = legalMoves[i];
        if (stopRequested) return bestScore || 0;

        const undo = makeMove(state, move);

        let score;

        if (i >= FULL_DEPTH_MOVES && depth > REDUCTION_LIMIT) {
            score = -(await negamax(state, depth - 2, -beta, -alpha));
            if (score > alpha) {
                score = -(await negamax(state, depth - 1, -beta, -alpha));
            }
        } else {
            score = -(await negamax(state, depth - 1, -beta, -alpha));
        }

        unmakeMove(state, undo);

        if (score > bestScore) bestScore = score;
        if (score > alpha) alpha = score;
        if (alpha >= beta) break;
    }

    if (transpositionTable.size < MAX_TT_SIZE) {
        transpositionTable.set(ttKey, { score: bestScore, depth });
    }

    return bestScore;
}

// ── Entry points (signatures unchanged for analysisWorker.js) ────────────────

export async function getTopMoves(position, whiteGold, blackGold, whiteIsActive, depth, amountOfMoves) {
    const state = createBoardState(position, whiteGold, blackGold, whiteIsActive);
    const legalMoves = getAllLegalMoves(state);

    if (legalMoves.length === 0) return null;

    let results = [];

    for (const move of legalMoves) {
        if (stopRequested) break;

        const undo = makeMove(state, move);
        const score = -(await negamax(state, depth - 1, -Infinity, Infinity));
        unmakeMove(state, undo);

        results.push({ move, score: whiteIsActive ? score : -score });

        await new Promise(r => setTimeout(r, 0));
    }

    if (whiteIsActive) return results.sort((a, b) => b.score - a.score).slice(0, amountOfMoves);
    else return results.sort((a, b) => a.score - b.score).slice(0, amountOfMoves);
}

export async function getBestMove(position, whiteGold, blackGold, whiteIsActive, maxPlayouts) {
    const state = createBoardState(position, whiteGold, blackGold, whiteIsActive);
    const legalMoves = getAllLegalMoves(state);

    if (legalMoves.length === 0) return null;

    // Estimate branching factor from both sides
    state.whiteIsActive = !state.whiteIsActive;
    const opponentMoves = getAllLegalMoves(state);
    state.whiteIsActive = !state.whiteIsActive;

    let branchingBase = (legalMoves.length + opponentMoves.length) / 2;
    branchingBase = branchingBase > 8 ? branchingBase : 8;

    let depth = MAX_DEPTH + 1;
    let estimatedPlayouts = Infinity;

    while (estimatedPlayouts > maxPlayouts) {
        depth--;
        estimatedPlayouts = Math.pow(branchingBase, depth);
    }

    let bestMove = null;
    let bestScore = -Infinity;

    for (const move of legalMoves) {
        const undo = makeMove(state, move);
        const score = -(await negamax(state, depth - 1, -Infinity, Infinity));
        unmakeMove(state, undo);

        if (score > bestScore) {
            bestScore = score;
            bestMove = move;
        }

        await Promise.resolve();
    }

    return bestMove;
}
