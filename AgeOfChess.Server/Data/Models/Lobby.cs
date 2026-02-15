namespace AgeOfChess.Server.Data.Models;

public enum LobbyStatus { Open, InProgress, Closed }

public class Lobby
{
    public int Id { get; set; }
    public int HostUserId { get; set; }
    public User Host { get; set; } = null!;
    public string MapSeed { get; set; } = string.Empty;
    public bool BiddingEnabled { get; set; }
    public int TimeLimitSeconds { get; set; } = 0; // 0 = no limit
    public LobbyStatus Status { get; set; } = LobbyStatus.Open;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public GameSession? GameSession { get; set; }
}
