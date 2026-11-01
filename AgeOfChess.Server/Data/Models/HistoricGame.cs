using AgeOfChess.Server.GameLogic;

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

    /// <summary>The game's settings, stored as columns of this table (see AppDbContext).</summary>
    public GameSettings Settings { get; set; } = new();

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
