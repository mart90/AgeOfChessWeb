using AgeOfChess.Server.GameLogic.PlaceableObjects;
using AgeOfChess.Server.GameLogic.PlaceableObjects.GaiaObjects;
using AgeOfChess.Server.GameLogic.PlaceableObjects.Pieces;

namespace AgeOfChess.Server.GameLogic;

public class Move
{
    public int? SourceSquareX { get; set; }
    public int? SourceSquareY { get; set; }

    public int DestinationSquareX { get; set; }
    public int DestinationSquareY { get; set; }

    public string? PiecePlaced { get; set; }
    public string? ObjectCaptured { get; set; }
    public string? Suffix { get; set; }   // "+" for check, "#" for checkmate/stalemate

    public string? SourceSquareStr => SourceSquareX != null ? $"{XToLetter(SourceSquareX.Value)}{SourceSquareY!.Value + 1}" : null;
    public string DestinationSquareStr => $"{XToLetter(DestinationSquareX)}{DestinationSquareY + 1}";

    public Move(string? sourceSquare, string destinationSquare, string? piecePlaced = null, string? objectCaptured = null)
    {
        (DestinationSquareX, DestinationSquareY) = SquareStringToXY(destinationSquare);

        if (sourceSquare != null)
            (SourceSquareX, SourceSquareY) = SquareStringToXY(sourceSquare);

        PiecePlaced = piecePlaced;
        ObjectCaptured = objectCaptured;
    }

    public Move(Map.Square? sourceSquare, Map.Square destinationSquare, string? piecePlaced = null, string? objectCaptured = null)
    {
        if (sourceSquare != null)
        {
            SourceSquareX = sourceSquare.X;
            SourceSquareY = sourceSquare.Y;
        }

        DestinationSquareX = destinationSquare.X;
        DestinationSquareY = destinationSquare.Y;

        PiecePlaced = piecePlaced;
        ObjectCaptured = objectCaptured;
    }

    public string ToNotation()
    {
        string body;
        if (PiecePlaced != null)
        {
            string pieceDisplay = PiecePlaced == "p" ? "p" : PiecePlaced.ToUpper();
            body = $"{XToLetter(DestinationSquareX)}{DestinationSquareY + 1}={pieceDisplay}";
        }
        else
        {
            string connector = ObjectCaptured != null ? "x" : "-";
            body = $"{XToLetter(SourceSquareX!.Value)}{SourceSquareY!.Value + 1}{connector}{XToLetter(DestinationSquareX)}{DestinationSquareY + 1}";
        }
        return body + (Suffix ?? "");
    }

    public static string ObjectTypeToStringId(Type placeableObjectType)
    {
        if (placeableObjectType == typeof(Treasure)) return "t";
        if (placeableObjectType == typeof(Queen)) return "q";
        if (placeableObjectType == typeof(Rook)) return "r";
        if (placeableObjectType == typeof(Bishop)) return "b";
        if (placeableObjectType == typeof(Knight)) return "n";
        if (placeableObjectType == typeof(Pawn)) return "p";
        throw new NotImplementedException($"No string id for type {placeableObjectType.Name}");
    }

    public static string ObjectToStringId(PlaceableObject obj)
    {
        if (obj is Treasure) return "t";
        if (obj is Queen) return "q";
        if (obj is Rook) return "r";
        if (obj is Bishop) return "b";
        if (obj is Knight) return "n";
        if (obj is Pawn) return "p";
        throw new NotImplementedException($"No string id for object {obj.GetType().Name}");
    }

    private static (int x, int y) SquareStringToXY(string squareString)
    {
        int x = LetterToX(squareString[0]);
        int y = int.Parse(squareString[1..]) - 1;
        return (x, y);
    }

    private static string XToLetter(int x) => ((char)(x + 97)).ToString();

    private static int LetterToX(char letter) => letter - 97;
}
