using AgeOfChess.Server.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace AgeOfChess.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<GameSession> GameSessions => Set<GameSession>();
    public DbSet<HistoricGame> HistoricGames => Set<HistoricGame>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();

        // MySQL allows multiple NULLs in a unique index, so this correctly
        // enforces that no two accounts share the same Google Subject ID.
        modelBuilder.Entity<User>()
            .HasIndex(u => u.GoogleId)
            .IsUnique();

        modelBuilder.Entity<GameSession>()
            .HasIndex(g => g.WhitePlayerToken)
            .IsUnique();

        modelBuilder.Entity<GameSession>()
            .HasIndex(g => g.BlackPlayerToken)
            .IsUnique();
    }
}
