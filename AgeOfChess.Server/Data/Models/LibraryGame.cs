namespace AgeOfChess.Server.Data.Models;

/// <summary>
/// A game (with any analysis variations) saved to a user's library ("My library" page).
/// The move tree is stored as positions, not just moves, so entries stay viewable when the rules
/// change. <see cref="DataJson"/> is the client's packed tree format (client/src/lib/gameTransfer.js):
/// { terrain: [[x, y, type]], tree: { san, s: { p, o, wg, bg, wa }, m, c: [...] } }.
/// </summary>
public class LibraryGame
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public string Title { get; set; } = string.Empty;

    /// <summary>Id of the original game, when saved from a finished game.</summary>
    public int? GameId { get; set; }

    public int MapSize { get; set; }
    public string? MapSeed { get; set; }

    /// <summary>Length of the mainline in plies.</summary>
    public int PlyCount { get; set; }

    /// <summary>Game result like "w+c" / "b+g", if known.</summary>
    public string? Result { get; set; }

    public string DataJson { get; set; } = string.Empty;

    /// <summary>Public id in share links (/analyze/{ShareId}); the numeric Id stays private.</summary>
    public Guid ShareId { get; set; } = Guid.NewGuid();

    /// <summary>Published: anyone with the share link can open it (and save a copy to their own library).</summary>
    public bool IsPublic { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
