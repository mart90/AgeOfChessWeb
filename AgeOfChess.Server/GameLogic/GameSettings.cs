namespace AgeOfChess.Server.GameLogic;

public class GameSettings
{
    public int BoardSize { get; set; } = 12;
    public bool TimeControlEnabled { get; set; } = true;
    public int StartTimeMinutes { get; set; } = 5;
    public int TimeIncrementSeconds { get; set; } = 5;
    public bool BiddingEnabled { get; set; } = false;
    public string? MapSeed { get; set; }
    public string MapMode { get; set; } = "m";

    /// <summary>Unranked games are stored in history but don't change ratings.</summary>
    public bool IsRanked { get; set; } = true;

    /// <summary>Computer opponent level (see ComputerLevels); null for games between humans.</summary>
    public int? ComputerLevel { get; set; }

    /// <summary>Which side the computer plays (only meaningful when ComputerLevel is set).</summary>
    public bool ComputerIsWhite { get; set; }
}
