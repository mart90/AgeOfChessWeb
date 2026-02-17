namespace AgeOfChess.Server.Data.Models;

public class User
{
    public int Id { get; set; }

    public string Username { get; set; } = string.Empty;

    /// <summary>Optional custom display name. Null means fall back to Username.</summary>
    public string? DisplayName { get; set; }

    public string? Email { get; set; }

    /// <summary>BCrypt hash; null for Google-only accounts.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Google Subject ID (sub claim); null for password accounts.</summary>
    public string? GoogleId { get; set; }

    // ── Per-category Elo ratings ─────────────────────────────────────────────
    public int EloBlitz { get; set; } = 1200;
    public int EloRapid { get; set; } = 1200;
    public int EloSlow  { get; set; } = 1200;

    // ── Per-category games played (used for K-factor decay) ──────────────────
    public int BlitzGamesPlayed { get; set; } = 0;
    public int RapidGamesPlayed { get; set; } = 0;
    public int SlowGamesPlayed  { get; set; } = 0;

    /// <summary>Total games across all categories.</summary>
    public int GamesPlayed { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Effective display name — falls back to Username when DisplayName is null.</summary>
    public string EffectiveDisplayName => DisplayName ?? Username;
}
