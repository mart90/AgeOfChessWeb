namespace AgeOfChess.Server.Data.Models;

public enum GameResult
{
    InProgress,
    WhiteWinMate, BlackWinMate,
    WhiteWinGold, BlackWinGold,
    WhiteWinStalemate, BlackWinStalemate,
    WhiteWinTime, BlackWinTime,
    WhiteWinResign, BlackWinResign
}

public class GameSession
{
    public int Id { get; set; }

    // Null for anonymous players
    public int? WhiteUserId { get; set; }
    public User? WhiteUser { get; set; }
    public int? BlackUserId { get; set; }
    public User? BlackUser { get; set; }

    // UUID tokens stored in the players' browsers - used to identify them in
    // SignalR and to reconnect if they lose the connection
    public string WhitePlayerToken { get; set; } = Guid.NewGuid().ToString("N");
    public string BlackPlayerToken { get; set; } = Guid.NewGuid().ToString("N");

    public string MapSeed { get; set; } = string.Empty;

    // Serialized GameSettings used to create this game (JSON)
    public string SettingsJson { get; set; } = "{}";

    public bool IsPrivate { get; set; } = false;

    // Denormalized from SettingsJson so EF Core can filter/sort at the SQL level
    public int  BoardSize            { get; set; } = 12;
    public bool TimeControlEnabled   { get; set; } = true;
    public int  StartTimeMinutes     { get; set; } = 5;
    public int  TimeIncrementSeconds { get; set; } = 5;

    // Updated on every move persist so we can sort by it in SQL
    public int  MoveCount { get; set; } = 0;

    // Serialized list of move notation strings, e.g. ["a1-b2", "c3xd4", "e5q"]
    public string MovesJson { get; set; } = "[]";

    public GameResult Result { get; set; } = GameResult.InProgress;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }

    // Elo ratings at the start of this game and the delta applied when it ended.
    // Null for anonymous players or games that didn't affect ratings.
    public int? WhiteEloAtGame { get; set; }
    public int? BlackEloAtGame { get; set; }
    public int? WhiteEloDelta  { get; set; }
    public int? BlackEloDelta  { get; set; }

    // Slow-game persistence — set once bidding resolves (or game starts for non-bidding games).
    // Null means "use default starting gold" (10g for non-bidding games).
    public int? BlackStartingGold { get; set; }

    // Updated after every move in slow games. Null for no-timer games or when no move has been made.
    public int? WhiteTimeMsRemaining { get; set; }
    public int? BlackTimeMsRemaining { get; set; }
}
