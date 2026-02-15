namespace AgeOfChess.Server.GameLogic;

public class PieceColor
{
    public bool IsWhite { get; }
    public bool IsActive { get; set; }
    public int Gold { get; set; }
    public int TimeMiliseconds { get; set; }
    public string PlayedByStr { get; set; }

    public PieceColor(bool isWhite, string playedByStr)
    {
        IsWhite = isWhite;
        PlayedByStr = playedByStr;
        IsActive = isWhite;
        Gold = isWhite ? 0 : 10;
    }
}
