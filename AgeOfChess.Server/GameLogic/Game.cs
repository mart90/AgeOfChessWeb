using AgeOfChess.Server.GameLogic.Map;
using AgeOfChess.Server.GameLogic.PlaceableObjects;
using AgeOfChess.Server.GameLogic.PlaceableObjects.GaiaObjects;
using AgeOfChess.Server.GameLogic.PlaceableObjects.Pieces;

namespace AgeOfChess.Server.GameLogic;

public abstract class Game
{
    public List<PieceColor> Colors { get; set; } = new();
    public GameState State { get; set; } = GameState.Default;
    public string? Result { get; protected set; }
    public bool GameEnded { get; protected set; }
    public List<Move> MoveList { get; } = new();
    public DateTime? LastMoveTimestamp { get; protected set; }

    protected Map.Map Map = null!;
    protected bool TimeControlEnabled;
    protected int TimeIncrementSeconds;
    protected bool FirstMoveMade;

    private static readonly Dictionary<Type, int> PieceCosts = new()
    {
        [typeof(Queen)]  = 70,
        [typeof(Rook)]   = 35,
        [typeof(Bishop)] = 25,
        [typeof(Knight)] = 30,
        [typeof(Pawn)]   = 20,
    };

    public int MapSize => Map.Width;
    public Map.Map GetMap() => Map;
    public PieceColor ActiveColor => Colors.Single(e => e.IsActive);
    public PieceColor InActiveColor => Colors.Single(e => !e.IsActive);
    public PieceColor White => Colors.Single(e => e.IsWhite);
    public PieceColor Black => Colors.Single(e => !e.IsWhite);
    public PieceColor GetPieceColor(Piece piece) => Colors.Single(e => e.IsWhite == piece.IsWhite);

    public static int GetPieceCost(Type pieceType) => PieceCosts[pieceType];

    protected virtual void EndGame()
    {
        GameEnded = true;
    }

    public virtual void EndTurn()
    {
        PieceColor previousActiveColor = ActiveColor;
        previousActiveColor.IsActive = false;
        Colors.Single(e => e != previousActiveColor).IsActive = true;

        foreach (Square square in Map.GetMines())
        {
            if (square.Object is Piece piece)
                GetPieceColor(piece).Gold += 3;
        }

        if (TimeControlEnabled)
        {
            previousActiveColor.TimeMiliseconds += TimeIncrementSeconds * 1000;
        }

        LastMoveTimestamp = DateTime.UtcNow;
    }

    public void StartNewTurn()
    {
        Map.Squares.ForEach(e => e.ClearTemporaryColor());
        State = GameState.Default;

        if (!FirstMoveMade)
            FirstMoveMade = true;

        if (CheckForMate())
        {
            EndGame();
            return;
        }

        if (CheckForGoldVictory() != null)
            EndGame();
    }

    /// <summary>
    /// Validates and executes a piece move. Returns false if the move is illegal.
    /// </summary>
    public bool TryMovePiece(int fromX, int fromY, int toX, int toY)
    {
        var sourceSquare = Map.GetSquareByCoordinates(fromX, fromY);
        var destinationSquare = Map.GetSquareByCoordinates(toX, toY);

        if (sourceSquare == null || destinationSquare == null) return false;
        if (sourceSquare.Object is not Piece piece) return false;
        if (GetPieceColor(piece) != ActiveColor) return false;

        var legalDestinations = Map.FindLegalDestinationsForPiece(piece, sourceSquare);
        if (!legalDestinations.Contains(destinationSquare)) return false;

        MovePiece(sourceSquare, destinationSquare);
        return true;
    }

    /// <summary>
    /// Validates and executes a piece placement. Returns false if the placement is illegal.
    /// </summary>
    public bool TryPlacePiece(int toX, int toY, Type pieceType)
    {
        if (!PieceCosts.ContainsKey(pieceType)) return false;

        var destinationSquare = Map.GetSquareByCoordinates(toX, toY);
        if (destinationSquare == null) return false;

        int cost = PieceCosts[pieceType];
        if (ActiveColor.Gold < cost) return false;

        var pathFinder = new PathFinder(Map);
        if (!pathFinder.FindLegalDestinationsForPiecePlacement(ActiveColor.IsWhite, pieceType == typeof(Pawn)).Contains(destinationSquare))
            return false;

        PlacePiece(destinationSquare, pieceType);
        return true;
    }

    protected void MovePiece(Square sourceSquare, Square destinationSquare)
    {
        PlaceableObject? objectToCapture = destinationSquare.Object;
        MoveList.Add(new Move(sourceSquare, destinationSquare, null, objectToCapture != null ? Move.ObjectToStringId(objectToCapture) : null));

        var piece = (Piece)sourceSquare.Object!;
        sourceSquare.ClearObject();

        if (destinationSquare.Object is Treasure)
            GetPieceColor(piece).Gold += 20;

        destinationSquare.SetObject(piece);
    }

    protected void PlacePiece(Square destinationSquare, Type pieceType)
    {
        MoveList.Add(new Move(null, destinationSquare, Move.ObjectTypeToStringId(pieceType)));

        Piece newPiece = pieceType switch
        {
            _ when pieceType == typeof(Queen)  => ActiveColor.IsWhite ? new WhiteQueen()  : new BlackQueen(),
            _ when pieceType == typeof(Rook)   => ActiveColor.IsWhite ? new WhiteRook()   : new BlackRook(),
            _ when pieceType == typeof(Bishop) => ActiveColor.IsWhite ? new WhiteBishop() : new BlackBishop(),
            _ when pieceType == typeof(Knight) => ActiveColor.IsWhite ? new WhiteKnight() : new BlackKnight(),
            _ when pieceType == typeof(Pawn)   => ActiveColor.IsWhite ? new WhitePawn()   : new BlackPawn(),
            _ => throw new ArgumentException($"Unknown piece type: {pieceType.Name}")
        };

        destinationSquare.SetObject(newPiece);
        ActiveColor.Gold -= PieceCosts[pieceType];
    }

    private PieceColor? CheckForGoldVictory()
    {
        var colorsWithWinningGold = Colors.Where(e => e.Gold > 150).ToList();
        if (!colorsWithWinningGold.Any()) return null;

        PieceColor? winningColor = null;

        if (colorsWithWinningGold.Count == 1)
        {
            winningColor = colorsWithWinningGold[0];
        }
        else if (colorsWithWinningGold.Select(e => e.Gold).Distinct().Count() == 2)
        {
            winningColor = colorsWithWinningGold.Single(e => e.Gold == colorsWithWinningGold.Max(c => c.Gold));
        }

        if (winningColor != null)
            Result = winningColor.IsWhite ? "w+g" : "b+g";

        return winningColor;
    }

    private bool CheckForMate()
    {
        var pathFinder = new PathFinder(Map);
        Square activeKingSquare = Map.Squares.Single(e => e.Object is King king && king.IsWhite == ActiveColor.IsWhite);

        var activeKingLegalMoves = pathFinder.FindLegalDestinationSquares((King)activeKingSquare.Object!, activeKingSquare);
        var legalCheckSquares = pathFinder.FindAttacksForColor(!ActiveColor.IsWhite);

        if (legalCheckSquares.Contains(activeKingSquare))
        {
            if (!activeKingLegalMoves.Any())
            {
                // Checkmate
                activeKingSquare.SetObject(new WhiteFlag());
                Result = ActiveColor.IsWhite ? "b+c" : "w+c";
                return true;
            }
            else
            {
                // In check — mark the king square red for the client
                activeKingSquare.SetTemporaryColor(SquareColor.Red);
                return false;
            }
        }

        // Stalemate: only king remains AND opponent has < 15 gold
        int activePieceCount = Map.Squares.Count(e => e.Object is Piece piece && piece.IsWhite == ActiveColor.IsWhite);
        if (!activeKingLegalMoves.Any() && activePieceCount == 1 && InActiveColor.Gold < 15)
        {
            activeKingSquare.SetObject(new WhiteFlag());
            Result = ActiveColor.IsWhite ? "b+s" : "w+s";
            return true;
        }

        return false;
    }
}
