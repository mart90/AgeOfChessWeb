namespace AgeOfChess.Server.GameLogic;

/// <summary>
/// A game's options. Stored as columns of GameSessions / HistoricGames (an EF owned type, see
/// AppDbContext); a ServerGame works on its own copy (<see cref="Clone"/>). Options that don't
/// apply to a game are null (see <see cref="Normalize"/>).
/// </summary>
public class GameSettings
{
    public int BoardSize { get; set; } = 12;
    public bool TimeControlEnabled { get; set; } = true;
    /// <summary>Null without time control.</summary>
    public int? StartTimeMinutes { get; set; } = 5;
    public int? TimeIncrementSeconds { get; set; } = 5;
    public bool BiddingEnabled { get; set; } = false;
    /// <summary>A seed the creator asked for (null: generate a board). The game's actual seed is
    /// GameSession.MapSeed / HistoricGame.MapSeed.</summary>
    public string? MapSeed { get; set; }
    public string MapMode { get; set; } = "m";

    /// <summary>Unranked games are stored in history but don't change ratings.</summary>
    public bool IsRanked { get; set; } = true;

    /// <summary>Computer opponent level (see ComputerLevels); null for games between humans.</summary>
    public int? ComputerLevel { get; set; }

    /// <summary>Which side the computer plays; null for games between humans.</summary>
    public bool? ComputerIsWhite { get; set; }

    /// <summary>
    /// Lobby games: the creator's colour, "white" | "black" | "random". The creator holds the White
    /// token until the game starts, when ServerGame.ApplyCreatorColor swaps if needed. Null for other
    /// games (matchmaking, rematches, vs computer) and with bidding.
    /// </summary>
    public string? CreatorColor { get; set; }

    /// <summary>
    /// Handicap gold: "none" | "auto" (from the Elo difference) | "manual" (HandicapGold). Without
    /// bidding it goes to Black (auto: negative when Black is the stronger player); with bidding,
    /// after the bids, to the lower-rated player. See ServerGame.ResolveHandicap.
    /// </summary>
    public string Handicap { get; set; } = "none";
    /// <summary>The manual amount; null unless Handicap is "manual".</summary>
    public int? HandicapGold { get; set; }

    /// <summary>
    /// The resolved handicap: gold added to each side's starting gold when the game started.
    /// Stored so replays and resumed games start with the same gold.
    /// </summary>
    public int WhiteHandicapGold { get; set; }
    public int BlackHandicapGold { get; set; }

    public bool HasHandicap => Handicap is "auto" or "manual";

    /// <summary>Clears the options that don't apply to this game. Called when a game is created.</summary>
    public void Normalize()
    {
        if (!TimeControlEnabled) StartTimeMinutes = TimeIncrementSeconds = null;
        if (ComputerLevel == null) ComputerIsWhite = null;
        if (ComputerLevel != null || BiddingEnabled) CreatorColor = null;
        if (Handicap != "manual") HandicapGold = null;
    }

    public GameSettings Clone() => (GameSettings)MemberwiseClone();
}
