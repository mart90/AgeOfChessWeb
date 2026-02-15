namespace AgeOfChess.Server.GameLogic.PlaceableObjects;

public class Piece : PlaceableObject
{
    public bool IsWhite { get; }

    public Piece(bool isWhite)
    {
        IsWhite = isWhite;
    }
}
