namespace AgeOfChess.Server.Data.Models;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public int EloRapid { get; set; } = 1200;
    public int EloBlitz { get; set; } = 1200;
    public int GamesPlayed { get; set; } = 0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
