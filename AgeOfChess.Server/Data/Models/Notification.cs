namespace AgeOfChess.Server.Data.Models;

/// <summary>
/// A notification for a user, shown under the bell in the nav bar until read
/// (see Services/NotificationService). Read ones are deleted after a while.
/// </summary>
public class Notification
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>What happened: <see cref="Types"/>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The game it's about, if any.</summary>
    public int? GameId { get; set; }

    /// <summary>The other player's name, for the text ("Game started vs …").</summary>
    public string? OpponentName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }

    public static class Types
    {
        /// <summary>Someone joined the user's lobby while they were elsewhere.</summary>
        public const string GameStarted = "gameStarted";
    }
}
