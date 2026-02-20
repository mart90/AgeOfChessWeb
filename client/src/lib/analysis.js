import { computeLegalMoves } from "./pathFinder";
import { SHOP } from './shop'

const MAX_DEPTH = 10;
const MAX_PLAYOUTS = 3 * Math.pow(10, 6);

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
export function findMoves(position, whiteGold, blackGold, whiteIsActive) {
    let bestChild = getBestImmediateMove(position, whiteGold, blackGold, whiteIsActive)
    return bestChild;
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

function isMajorPiece(pieceType) {
    return (
        pieceType.includes("Queen") ||
        pieceType.includes("Rook") ||
        pieceType.includes("Bishop") ||
        pieceType.includes("Knight")
    );
}

function isValidPlacementSquare(square) {
    return (
        square.piece == null &&
        (
            square.type === "Grass" ||
            square.type === "GrassTrees" ||
            square.type === "GrassMine" ||
            square.type === "Dirt" ||
            square.type === "DirtTrees" ||
            square.type === "DirtMine"
        )
    );
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

function clonePosition(position) {
    return position.map(square => ({
        ...square,
        piece: square.piece ? { ...square.piece } : null
    }));
}

function applyMove(position, whiteGold, blackGold, whiteIsActive, move) {
    let newPosition = clonePosition(position);

    if (move.type == "m") { 
        // Move
        const piece = position.find(s => s.x === move.fromPos.x && s.y === move.fromPos.y).piece;
        const toSq = position.find(s => s.x === move.toPos.x && s.y === move.toPos.y);

        if (toSq?.piece) {
            if (toSq.piece.type == "Treasure") {
                if (piece.isWhite) whiteGold += 20; else blackGold += 20;
            }
        }

        newPosition = newPosition.map(s => {
            if (s.x === move.fromPos.x && s.y === move.fromPos.y) return { ...s, piece: null };
            if (s.x === move.toPos.x   && s.y === move.toPos.y)   return { ...s, piece };
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

        newPosition = newPosition.map(s => {
            if (s.x === move.toPos.x   && s.y === move.toPos.y) return { ...s, piece };
            return s;
        });
    }

    // Mine income for newly active player
    const nextWhite = !whiteIsActive;
    const income = newPosition
        .filter(s => s.type?.includes('Mine') && s.piece?.isWhite === nextWhite)
        .length * 5;
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
    if (pieceType.includes("Queen")) {
        return 95;
    }
    if (pieceType.includes("Rook")) {
        return 52;
    }
    if (pieceType.includes("Bishop")) {
        return 40;
    }
    if (pieceType.includes("Knight")) {
        return 45;
    }
    if (pieceType.includes("Pawn")) {
        return 35;
    }

    return 0;
}

function makeMove(position, move) {
    if (move.type === "m") {
        const from = position.find(s => s.x === move.fromPos.x && s.y === move.fromPos.y);
        const to   = position.find(s => s.x === move.toPos.x && s.y === move.toPos.y);

        const movedPiece = from.piece;
        const capturedPiece = to.piece;

        // Apply move
        to.piece = movedPiece;
        from.piece = null;

        return {
            type: "m",
            from,
            to,
            capturedPiece
        };
    }

    if (move.type === "p") {
        const square = position.find(s => s.x === move.pos.x && s.y === move.pos.y);

        square.piece = {
            type: move.pieceType,
            isWhite: move.isWhite
        };

        return {
            type: "p",
            square
        };
    }
}

function undoMove(position, undo) {
    if (undo.type === "m") {
        undo.from.piece = undo.to.piece;
        undo.to.piece = undo.capturedPiece;
    }

    if (undo.type === "p") {
        undo.square.piece = null;
    }
}

function getBestImmediateMove(position, whiteGold, blackGold, whiteIsActive) 
{
    const legalMoves = getAllLegalMoves(position, whiteGold, blackGold, whiteIsActive);

    if (legalMoves.length === 0) 
        return null; 

    const legalMovesOpponent = getAllLegalMoves(position, whiteGold, blackGold, !whiteIsActive);
    let branchingBase = (legalMoves.length + legalMovesOpponent.length) / 2;
    branchingBase = branchingBase > 8 ? branchingBase : 8;

    let depth = MAX_DEPTH + 1;
    let estimatedPlayouts = Infinity;

    while (estimatedPlayouts > MAX_PLAYOUTS) {
        depth--;
        estimatedPlayouts = Math.pow(branchingBase, depth);
    }

    console.log("Searching with depth " + depth);
    
    let bestMove = null; 
    let bestScore = -Infinity; 
    
    for (let move of legalMoves) 
    { 
        const { newPosition, newWhiteGold, newBlackGold } = applyMove(position, whiteGold, blackGold, whiteIsActive, move);

        const score = -negamax(
            newPosition,
            newWhiteGold,
            newBlackGold,
            !whiteIsActive,
            depth - 1,
            -Infinity,
            Infinity
        );
        
        if (score > bestScore) { 
            bestScore = score; 
            bestMove = move; 
        } 
    } 
    
    console.log("Position score: " + (whiteIsActive ? bestScore : -bestScore));
    return bestMove; 
}

function negamax(position, whiteGold, blackGold, whiteIsActive, depth, alpha, beta) {

    const evalScore = evaluate(position, whiteGold, blackGold);

    // Terminal or depth limit
    if (depth === 0 || Math.abs(evalScore) > 900) {
        return whiteIsActive ? evalScore : -evalScore;
    }

    const legalMoves = getAllLegalMoves(position, whiteGold, blackGold, whiteIsActive);

    if (legalMoves.length === 0) {
        return whiteIsActive ? evalScore : -evalScore;
    }

    // legalMoves.sort((a, b) => {
    //     const A = applyMove(position, whiteGold, blackGold, whiteIsActive, a);
    //     const B = applyMove(position, whiteGold, blackGold, whiteIsActive, b);

    //     const scoreA = evaluate(A.newPosition, A.newWhiteGold, A.newBlackGold);
    //     const scoreB = evaluate(B.newPosition, B.newWhiteGold, B.newBlackGold);

    //     // Sort best moves first
    //     return whiteIsActive ? scoreB - scoreA : scoreA - scoreB;
    // });

    let bestScore = -Infinity;

    for (let move of legalMoves) {
        const {
            newPosition,
            newWhiteGold,
            newBlackGold
        } = applyMove(position, whiteGold, blackGold, whiteIsActive, move);

        const score = -negamax(
            newPosition,
            newWhiteGold,
            newBlackGold,
            !whiteIsActive,
            depth - 1,
            -beta,
            -alpha
        );

        // const undo = makeMove(position, move);

        // const score = -negamax(
        //     position,
        //     depth - 1,
        //     -beta,
        //     -alpha,
        //     !whiteIsActive,
        //     whiteGold,
        //     blackGold
        // );

        // undoMove(position, undo);

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

    return bestScore;
}

function evaluate(position, whiteGold, blackGold) {
    let score = 0;

    let whiteMaterialValue = 0
    let blackMaterialValue = 0

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
    }

    if (!whiteHasKing) {
        score -= 999;
    }
    if (!blackHasKing) {
        score += 999;
    }

    score += (whiteMaterialValue + whiteGold) / 10;
    score -= (blackMaterialValue + blackGold) / 10;

    score += position.filter(s => s.type.includes("Mine") && s.piece != null && s.piece.isWhite).length;
    score -= position.filter(s => s.type.includes("Mine") && s.piece != null && !s.piece.isWhite).length;

    return score;
}