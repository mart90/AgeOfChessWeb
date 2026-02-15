namespace AgeOfChess.Server.Data.Models;

public enum LobbyStatus { Open, InProgress, Closed }

// Lobbies are for registered-user matchmaking only.
// Anonymous invite games bypass the lobby system entirely.
public class Lobby
{
    public int Id { get; set; }
    public int HostUserId { get; set; }
    public User Host { get; set; } = null!;
    public string SettingsJson { get; set; } = "{}";
    public LobbyStatus Status { get; set; } = LobbyStatus.Open;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public GameSession? GameSession { get; set; }
}
