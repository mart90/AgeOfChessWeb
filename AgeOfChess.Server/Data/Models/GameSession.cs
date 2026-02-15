namespace AgeOfChess.Server.Data.Models;

public enum GameResult { InProgress, WhiteWinMate, BlackWinMate, WhiteWinGold, BlackWinGold, WhiteWinStalemate, BlackWinStalemate, WhiteWinTime, BlackWinTime, WhiteWinResign, BlackWinResign }

public class GameSession
{
    public int Id { get; set; }
    public int LobbyId { get; set; }
    public Lobby Lobby { get; set; } = null!;
    public int WhiteUserId { get; set; }
    public User WhiteUser { get; set; } = null!;
    public int BlackUserId { get; set; }
    public User BlackUser { get; set; } = null!;

    // Serialized list of move strings (e.g. ["a1-b2", "c3xd4", ...])
    public string MovesJson { get; set; } = "[]";

    public GameResult Result { get; set; } = GameResult.InProgress;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
}
