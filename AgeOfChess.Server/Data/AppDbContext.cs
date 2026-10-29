using AgeOfChess.Server.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace AgeOfChess.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<GameSession> GameSessions => Set<GameSession>();
    public DbSet<HistoricGame> HistoricGames => Set<HistoricGame>();
    public DbSet<LibraryGame> LibraryGames => Set<LibraryGame>();

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

        modelBuilder.Entity<LibraryGame>(e =>
        {
            e.HasIndex(g => g.UserId);
            e.HasIndex(g => g.ShareId).IsUnique();
            e.Property(g => g.Title).HasMaxLength(200);
            e.Property(g => g.MapSeed).HasMaxLength(200);
            e.Property(g => g.Result).HasMaxLength(10);
            e.Property(g => g.DataJson).HasColumnType("longtext");
            e.HasOne(g => g.User).WithMany().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
