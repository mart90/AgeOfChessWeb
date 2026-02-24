import { computeLegalMoves } from "./pathFinder";
import { SHOP, MINE_INCOME } from './constants'

const MAX_DEPTH = 10;
let i = 0;

// position:
// [
//     {
//         x: 0,
//         y: 0,
//         type: "Grass",
//         piece: {
//             type: "WhiteKing",
//             isWhite: true
//         }
//     },
//     {
//         x: 0,
//         y: 1,
//         type: "GrassTrees",
//         piece: null
//     },
//     ...
// ]
export let stopRequested = false;
let nodeCount = 0;
const YIELD_INTERVAL = 1000; // Yield every 1000 nodes

// Transposition table for caching evaluated positions
const transpositionTable = new Map();
const MAX_TT_SIZE = 500000; // Limit table size to prevent memory issues

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

const PIECE_VALUES = {
    'WhiteQueen': 90, 'BlackQueen': 90,
    'WhiteRook': 52, 'BlackRook': 52,
    'WhiteBishop': 42, 'BlackBishop': 42,
    'WhiteKnight': 40, 'BlackKnight': 40,
    'WhitePawn': 25, 'BlackPawn': 25
};

// Create a hash key for a position (simple string-based for now)
function hashPosition(position, whiteGold, blackGold, whiteIsActive) {
    // Sort squares by position to ensure consistent hashing regardless of array order
    const sortedSquares = [...position].sort((a, b) => a.x === b.x ? a.y - b.y : a.x - b.x);

    const parts = sortedSquares.map(s => {
        if (!s.piece) return `${s.x},${s.y}:`;
        return `${s.x},${s.y}:${s.piece.type},${s.mineOwner || ''}`;
    });

    return `${parts.join('|')}|${whiteGold}|${blackGold}|${whiteIsActive ? 'w' : 'b'}`;
}

export async function getTopMoves(position, whiteGold, blackGold, whiteIsActive, depth, amountOfMoves)
{
    const legalMoves = getAllLegalMoves(position, whiteGold, blackGold, whiteIsActive);

    if (legalMoves.length === 0)
        return null;

    let results = [];

    for (let move of legalMoves)
    {
        if (stopRequested) {
            break;
        }

        const { newPosition, newWhiteGold, newBlackGold } = applyMove(position, whiteGold, blackGold, whiteIsActive, move);

        const score = -(await negamax(
            newPosition,
            newWhiteGold,
            newBlackGold,
            !whiteIsActive,
            depth - 1,
            -Infinity,
            Infinity
        ));

        results.push({
            move,
            score: whiteIsActive ? score : -score
        });

        // Yield control so worker can process messages
        await new Promise(r => setTimeout(r, 0));
    }

    if (whiteIsActive) return results
        .sort((a, b) => b.score - a.score)
        .slice(0, amountOfMoves)
    else return results
        .sort((a, b) => a.score - b.score)
        .slice(0, amountOfMoves)
}

export async function getBestMove(position, whiteGold, blackGold, whiteIsActive, maxPlayouts) 
{
    const legalMoves = getAllLegalMoves(position, whiteGold, blackGold, whiteIsActive);

    if (legalMoves.length === 0) 
        return null; 

    const legalMovesOpponent = getAllLegalMoves(position, whiteGold, blackGold, !whiteIsActive);
    let branchingBase = (legalMoves.length + legalMovesOpponent.length) / 2;
    branchingBase = branchingBase > 8 ? branchingBase : 8;

    let depth = MAX_DEPTH + 1;
    let estimatedPlayouts = Infinity;

    while (estimatedPlayouts > maxPlayouts) {
        depth--;
        estimatedPlayouts = Math.pow(branchingBase, depth);
    }

    let bestMove = null; 
    let bestScore = -Infinity; 
    
    for (let move of legalMoves) 
    { 
        const { newPosition, newWhiteGold, newBlackGold } = applyMove(position, whiteGold, blackGold, whiteIsActive, move);

        const score = -(await negamax(
            newPosition,
            newWhiteGold,
            newBlackGold,
            !whiteIsActive,
            depth - 1,
            -Infinity,
            Infinity
        ));
        
        if (score > bestScore) { 
            bestScore = score; 
            bestMove = move; 
        }

        await Promise.resolve();
    }

    return bestMove; 
}

function createNode(position, whiteGold, blackGold, whiteIsActive, parent, move) {
    return {
        position,
        whiteGold,
        blackGold,
        whiteIsActive,
        parent,
        move,
        children: [],
        visits: 0,
        totalScore: 0,
        isTerminal: false
    };
}

const MAJOR_PIECES = new Set([
    'WhiteQueen', 'BlackQueen',
    'WhiteRook', 'BlackRook',
    'WhiteBishop', 'BlackBishop',
    'WhiteKnight', 'BlackKnight'
]);

function isMajorPiece(pieceType) {
    return MAJOR_PIECES.has(pieceType);
}

const VALID_PLACEMENT_TYPES = new Set([
    'Grass', 'GrassTrees', 'GrassMine',
    'Dirt', 'DirtTrees', 'DirtMine'
]);

function isValidPlacementSquare(square) {
    return square.piece == null && VALID_PLACEMENT_TYPES.has(square.type);
}

function getAdjacentSquares(position, x, y) {
    const deltas = [
        [-1, -1], [-1, 0], [-1, 1],
        [0, -1],           [0, 1],
        [1, -1],  [1, 0],  [1, 1]
    ];

    const result = [];

    for (let [dx, dy] of deltas) {
        const nx = x + dx;
        const ny = y + dy;

        const square = position.find(s => s.x === nx && s.y === ny);
        if (square) result.push(square);
    }

    return result;
}

function getAllLegalNewPiecePlacements(position, gold, whiteIsActive) {
    const placements = [];

    // Find our pieces
    const ourPieces = position.filter(
        s => s.piece && s.piece.isWhite === whiteIsActive
    );

    // Find our king
    const kingSquare = ourPieces.find(
        s => s.piece.type === (whiteIsActive ? "WhiteKing" : "BlackKing")
    );

    if (!kingSquare) return placements;

    // Precompute squares around king
    const kingAdjacent = getAdjacentSquares(position, kingSquare.x, kingSquare.y)
        .filter(isValidPlacementSquare);

    // Precompute squares around major pieces
    let majorAdjacents = [];

    for (let square of ourPieces) {
        if (isMajorPiece(square.piece.type)) {
            const adj = getAdjacentSquares(position, square.x, square.y)
                .filter(isValidPlacementSquare);

            majorAdjacents.push(...adj);
        }
    }

    // Remove duplicates
    const uniqueMajorAdjacents = [...new Map(
        majorAdjacents.map(s => [`${s.x},${s.y}`, s])
    ).values()];

    // Evaluate each shop piece
    for (let shopItem of SHOP) {
        const cost = shopItem.cost;

        if (gold < cost) continue;

        const pieceType = shopItem.type;

        let candidateSquares = [];

        if (pieceType.includes("Pawn")) {
            candidateSquares = [
                ...kingAdjacent,
                ...uniqueMajorAdjacents
            ];
        } else {
            candidateSquares = kingAdjacent;
        }

        // Remove duplicates again
        const uniqueSquares = [...new Map(
            candidateSquares.map(s => [`${s.x},${s.y}`, s])
        ).values()];

        for (let square of uniqueSquares) {
            placements.push({
                type: "p",
                toPos: {
                    x: square.x,
                    y: square.y
                },
                pieceType,
                cost
            });
        }
    }

    return placements;
}

function getAllLegalMoves(position, whiteGold, blackGold, whiteIsActive) {
    let moves = [];

    for (let square of position) {
        if (square.piece && square.piece.isWhite === whiteIsActive) {
            const pieceMoves = computeLegalMoves(square, position);
            for (let destination of pieceMoves) {
                moves.push({
                    type: "m",
                    fromPos: {
                        x: square.x,
                        y: square.y
                    },
                    toPos: {
                        x: destination.x,
                        y: destination.y
                    }
                })
            }
        }
    }

    if (whiteIsActive) {
        if (whiteGold >= 20) {
            moves.push(...getAllLegalNewPiecePlacements(position, whiteGold, true));
        }
    }
    else if (blackGold >= 20) {
        moves.push(...getAllLegalNewPiecePlacements(position, blackGold, false));
    }

    return moves;
}

function applyMove(position, whiteGold, blackGold, whiteIsActive, move) {
    let newPosition;

    if (move.type == "m") {
        // Move
        const piece = position.find(s => s.x === move.fromPos.x && s.y === move.fromPos.y).piece;
        const toSq = position.find(s => s.x === move.toPos.x && s.y === move.toPos.y);

        if (toSq?.piece) {
            if (toSq.piece.type == "Treasure") {
                if (piece.isWhite) whiteGold += 20; else blackGold += 20;
            }
        }

        newPosition = position.map(s => {
            if (s.x === move.fromPos.x && s.y === move.fromPos.y) return { ...s, piece: null };
            if (s.x === move.toPos.x   && s.y === move.toPos.y) {
                // Capture mine if moving onto one
                const capturedMine = s.type?.includes('Mine');
                return {
                    ...s,
                    piece,
                    mineOwner: capturedMine ? (piece.isWhite ? 'white' : 'black') : s.mineOwner
                };
            }
            return s;
        });
    }
    else {
        // New piece placement

        let pieceType;

        if (whiteIsActive) {
            whiteGold -= move.cost
            pieceType = "White" + move.pieceType;
        }
        else {
            blackGold -= move.cost
            pieceType = "Black" + move.pieceType;
        }

        let piece = {
            type: pieceType,
            isWhite: whiteIsActive
        }

        newPosition = position.map(s => {
            if (s.x === move.toPos.x   && s.y === move.toPos.y) {
                // Capture mine if placing on one
                const capturedMine = s.type?.includes('Mine');
                return {
                    ...s,
                    piece,
                    mineOwner: capturedMine ? (whiteIsActive ? 'white' : 'black') : s.mineOwner
                };
            }
            return s;
        });
    }

    // Mine income for newly active player (based on ownership, not occupancy)
    const nextWhite = !whiteIsActive;
    const income = newPosition
        .filter(s => s.type?.includes('Mine') && s.mineOwner === (nextWhite ? 'white' : 'black'))
        .length * MINE_INCOME;
    if (nextWhite) whiteGold += income; else blackGold += income;

    // 1 gold per turn
    if (nextWhite) whiteGold++; else blackGold++;
    
    return {
        newPosition,
        newWhiteGold: whiteGold,
        newBlackGold: blackGold
    };
}

function pieceValue(pieceType) {
    return PIECE_VALUES[pieceType] || 0;
}

async function negamax(position, whiteGold, blackGold, whiteIsActive, depth, alpha, beta) {
    if (stopRequested) {
        return 0;
    }

    // Yield periodically to allow worker to process messages
    nodeCount++;
    if (nodeCount % YIELD_INTERVAL === 0) {
        await new Promise(r => setTimeout(r, 0));
    }

    // Check transposition table
    const posHash = hashPosition(position, whiteGold, blackGold, whiteIsActive);
    const ttEntry = transpositionTable.get(posHash);
    if (ttEntry && ttEntry.depth >= depth) {
        return ttEntry.score;
    }

    const evalScore = evaluate(position, whiteGold, blackGold);

    // Terminal or depth limit
    if (depth === 0 || Math.abs(evalScore) > 900) {
        return whiteIsActive ? evalScore : -evalScore;
    }

    const legalMoves = getAllLegalMoves(position, whiteGold, blackGold, whiteIsActive);

    if (legalMoves.length === 0) {
        return whiteIsActive ? evalScore : -evalScore;
    }

    let bestScore = -Infinity;
    const FULL_DEPTH_MOVES = 4; // Search first 4 moves at full depth
    const REDUCTION_LIMIT = 3;  // Don't reduce if depth <= 3

    for (let i = 0; i < legalMoves.length; i++) {
        const move = legalMoves[i];
        if (stopRequested) return bestScore || 0;

        const {
            newPosition,
            newWhiteGold,
            newBlackGold
        } = applyMove(position, whiteGold, blackGold, whiteIsActive, move);

        let score;

        // Late Move Reductions: search later moves at reduced depth
        if (i >= FULL_DEPTH_MOVES && depth > REDUCTION_LIMIT) {
            // Search at reduced depth first
            score = -(await negamax(
                newPosition,
                newWhiteGold,
                newBlackGold,
                !whiteIsActive,
                depth - 2,  // Reduce by 1 extra ply
                -beta,
                -alpha
            ));

            // If the reduced search found something good, re-search at full depth
            if (score > alpha) {
                score = -(await negamax(
                    newPosition,
                    newWhiteGold,
                    newBlackGold,
                    !whiteIsActive,
                    depth - 1,
                    -beta,
                    -alpha
                ));
            }
        } else {
            // First few moves or shallow depth: search at full depth
            score = -(await negamax(
                newPosition,
                newWhiteGold,
                newBlackGold,
                !whiteIsActive,
                depth - 1,
                -beta,
                -alpha
            ));
        }

        if (score > bestScore) {
            bestScore = score;
        }

        if (score > alpha) {
            alpha = score;
        }

        if (alpha >= beta) {
            break;
        }
    }

    // Store in transposition table (limit size to prevent memory issues)
    if (transpositionTable.size < MAX_TT_SIZE) {
        transpositionTable.set(posHash, { score: bestScore, depth });
    }

    return bestScore;
}

function evaluate(position, whiteGold, blackGold) {
    let score = 0;

    let whiteMaterialValue = 0
    let blackMaterialValue = 0
    let whiteMines = 0;
    let blackMines = 0;

    let whiteHasKing = false;
    let blackHasKing = false;

    for (let square of position) {
        if (square.piece != null) {
            if (square.piece.type == "WhiteKing") {
                whiteHasKing = true;
                continue;
            }
            if (square.piece.type == "BlackKing") {
                blackHasKing = true;
                continue;
            }
            if (square.piece.isWhite) {
                whiteMaterialValue += pieceValue(square.piece.type) * 1.2;
            }
            else {
                blackMaterialValue += pieceValue(square.piece.type) * 1.2;
            }
        }

        // Count mines based on ownership, not occupancy
        if (square.type.includes("Mine")) {
            if (square.mineOwner === 'white') whiteMines++;
            else if (square.mineOwner === 'black') blackMines++;
        }
    }

    if (!whiteHasKing) {
        score -= 999;
    }
    if (!blackHasKing) {
        score += 999;
    }

    score += (whiteMaterialValue + whiteGold) / 5;
    score -= (blackMaterialValue + blackGold) / 5;

    score += whiteMines * (MINE_INCOME / 2);
    score -= blackMines * (MINE_INCOME / 2);

    return score;
}