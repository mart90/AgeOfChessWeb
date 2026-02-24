namespace AgeOfChess.Server.Data.Models;

public class HistoricGame
{
    public int Id { get; set; }

    // Players
    public int? WhitePlayerId { get; set; }
    public int? BlackPlayerId { get; set; }

    // Elo at game time
    public int? WhiteEloAtGame { get; set; }
    public int? BlackEloAtGame { get; set; }
    public int? WhiteEloDelta { get; set; }
    public int? BlackEloDelta { get; set; }

    // Settings (JSON)
    public string SettingsJson { get; set; } = "{}";

    // Denormalized from SettingsJson for SQL-level filtering and sorting
    public int  BoardSize            { get; set; } = 12;
    public bool TimeControlEnabled   { get; set; } = true;
    public int  StartTimeMinutes     { get; set; } = 5;
    public int  TimeIncrementSeconds { get; set; } = 5;

    // Bidding
    public int? WhiteBid { get; set; }
    public int? BlackBid { get; set; }

    // Map
    public string MapSeed { get; set; } = "";

    // Moves (JSON array of move strings)
    public string MovesJson { get; set; } = "[]";
    public int MoveCount { get; set; } = 0;

    // Result
    public string? Result { get; set; }

    // Timestamps
    public DateTime CreatedAt { get; set; }
    public DateTime? EndedAt { get; set; }
}
