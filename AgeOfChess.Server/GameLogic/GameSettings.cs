namespace AgeOfChess.Server.GameLogic;

public class GameSettings
{
    public int BoardSize { get; set; } = 12;
    public bool TimeControlEnabled { get; set; } = true;
    public int StartTimeMinutes { get; set; } = 5;
    public int TimeIncrementSeconds { get; set; } = 5;
    public bool BiddingEnabled { get; set; } = false;
    public string? MapSeed { get; set; }
}
