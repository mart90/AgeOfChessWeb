using System.Linq.Expressions;
using AgeOfChess.Server.Data.Models;
using AgeOfChess.Server.GameLogic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgeOfChess.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<GameSession> GameSessions => Set<GameSession>();
    public DbSet<HistoricGame> HistoricGames => Set<HistoricGame>();
    public DbSet<LibraryGame> LibraryGames => Set<LibraryGame>();
    public DbSet<Notification> Notifications => Set<Notification>();

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

        MapSettings(modelBuilder.Entity<GameSession>(), g => g.Settings);
        MapSettings(modelBuilder.Entity<HistoricGame>(), g => g.Settings);

        modelBuilder.Entity<GameSession>()
            .HasIndex(g => g.BlackPlayerToken)
            .IsUnique();

        // A player's games (profile page): "white or black" uses both
        modelBuilder.Entity<HistoricGame>(e =>
        {
            e.HasIndex(g => g.WhitePlayerId);
            e.HasIndex(g => g.BlackPlayerId);
        });

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

        modelBuilder.Entity<Notification>(e =>
        {
            e.HasIndex(n => new { n.UserId, n.ReadAt });
            e.HasIndex(n => n.GameId);
            e.Property(n => n.Type).HasMaxLength(32);
            e.Property(n => n.OpponentName).HasMaxLength(100);
            e.HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// All times are stored in UTC. MySQL keeps no time zone, so values read back would be
    /// "unspecified" and serialized without a "Z", which browsers take for local time.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcConverter>();
    }

    private class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    /// <summary>A game's settings as plain columns of the game's own table (no prefix).</summary>
    private static void MapSettings<T>(EntityTypeBuilder<T> entity, Expression<Func<T, GameSettings?>> settings)
        where T : class
    {
        entity.OwnsOne(settings, s =>
        {
            s.Property(x => x.BoardSize).HasColumnName("BoardSize");
            s.Property(x => x.TimeControlEnabled).HasColumnName("TimeControlEnabled");
            s.Property(x => x.StartTimeMinutes).HasColumnName("StartTimeMinutes");
            s.Property(x => x.TimeIncrementSeconds).HasColumnName("TimeIncrementSeconds");
            s.Property(x => x.BiddingEnabled).HasColumnName("BiddingEnabled");
            s.Property(x => x.MapSeed).HasColumnName("RequestedMapSeed");
            s.Property(x => x.MapMode).HasColumnName("MapMode").HasMaxLength(1);
            s.Property(x => x.IsRanked).HasColumnName("IsRanked");
            s.Property(x => x.ComputerLevel).HasColumnName("ComputerLevel");
            s.Property(x => x.ComputerIsWhite).HasColumnName("ComputerIsWhite");
            s.Property(x => x.CreatorColor).HasColumnName("CreatorColor").HasMaxLength(6);
            s.Property(x => x.Handicap).HasColumnName("Handicap").HasMaxLength(6);
            s.Property(x => x.HandicapGold).HasColumnName("HandicapGold");
            s.Property(x => x.WhiteHandicapGold).HasColumnName("WhiteHandicapGold");
            s.Property(x => x.BlackHandicapGold).HasColumnName("BlackHandicapGold");
        });
        entity.Navigation(settings).IsRequired();
    }
}
