using AgeOfChess.Server.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace AgeOfChess.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Lobby> Lobbies => Set<Lobby>();
    public DbSet<GameSession> GameSessions => Set<GameSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();

        modelBuilder.Entity<GameSession>()
            .HasOne(g => g.Lobby)
            .WithOne(l => l.GameSession)
            .HasForeignKey<GameSession>(g => g.LobbyId);
    }
}
