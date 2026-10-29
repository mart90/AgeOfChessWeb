using AgeOfChess.Server.Data;
using AgeOfChess.Server.Data.Models;
using AgeOfChess.Server.GameLogic;
using Microsoft.EntityFrameworkCore;

namespace AgeOfChess.Server.Services;

/// <summary>
/// Background service that ends abandoned games and evicts stale ones.
///
/// Every minute, per in-memory game:
///   • Timed game whose active player's clock has run out (+5 s grace, so a client normally claims
///     it first via ClaimTimeout) → ends on time like a claimed timeout (history, Elo). This covers
///     games both players left, where nobody is around to claim. Vs the computer, a flag on the
///     computer's side while the human is disconnected counts as "abandoned" instead.
///   • Untimed game, both players disconnected, no move for 7 days → ends as "abandoned" (no Elo)
///     and moves to history, so it is no longer resumed after a restart.
///   • Never started (waiting for the 2nd player) → removed after 2 h.
///   • Started but no move made, both players disconnected → removed after 30 min.
///   • Started, both players disconnected, no move for 24 h → removed (fallback, e.g. abandoned
///     during bidding).
///   • Ended → removed 15 min after the game ended (safety net for BroadcastState's 10-minute removal).
///
/// Every 30 minutes: deletes in-progress GameSession rows older than 1 h that have no in-memory
/// game (removed lobbies, timed games lost to a restart). They can never be resumed: every
/// resumable slow game is loaded into memory at startup.
/// </summary>
public class GameCleanupService(
    GameSessionManager sessions, GameFlowService flow, IServiceScopeFactory scopeFactory,
    ILogger<GameCleanupService> log) : BackgroundService
{
    private static readonly TimeSpan CheckInterval          = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DbSweepInterval        = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan NeverStartedLimit      = TimeSpan.FromHours(2);
    private static readonly TimeSpan NoMoveLimit            = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StaleStartedLimit      = TimeSpan.FromHours(24);
    private static readonly TimeSpan AbandonedUntimedLimit  = TimeSpan.FromDays(7);
    private static readonly TimeSpan EndedGameLimit         = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan OrphanRowMinAge        = TimeSpan.FromHours(1);
    private const int FlagGraceMs = 5000;

    // When this service first saw each game as ended (games have no end timestamp of their own)
    private readonly Dictionary<int, DateTime> _endedSeenAt = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger the first run so startup noise settles first.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        var lastSweep = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupGamesAsync();
                if (DateTime.UtcNow - lastSweep > DbSweepInterval)
                {
                    await SweepOrphanedSessionsAsync();
                    lastSweep = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Game cleanup failed");
            }
            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private async Task CleanupGamesAsync()
    {
        var now   = DateTime.UtcNow;
        var games = sessions.GetAll().ToList();

        foreach (var game in games)
        {
            using var _ = await game.LockAsync();

            bool bothDisconnected = !game.WhiteConnected && !game.BlackConnected;
            var  lastActivity     = game.LastMoveTimestamp ?? game.CreatedAt;

            if (game.GameEnded)
            {
                if (!_endedSeenAt.TryGetValue(game.SessionId, out var endedAt))
                    _endedSeenAt[game.SessionId] = endedAt = now;
                if (now - endedAt > EndedGameLimit)
                    sessions.Remove(game.SessionId);
            }
            else if (!game.HasGameStarted)
            {
                if (now - game.CreatedAt > NeverStartedLimit)
                    sessions.Remove(game.SessionId);
            }
            else if (FlagFallen(game, now))
            {
                var  active    = game.ActiveColor;
                bool humanGone = game.IsComputerGame
                              && !(game.Settings.ComputerIsWhite ? game.BlackConnected : game.WhiteConnected);
                active.TimeMiliseconds = 0;
                game.ForceEnd(humanGone && game.IsComputerSide(active.IsWhite)
                    ? "abandoned"
                    : active.IsWhite ? "b+t" : "w+t");
                await flow.BroadcastState(game);
                log.LogInformation("Game {Id} ended on time ({Result}) by cleanup", game.SessionId, game.Result);
            }
            else if (bothDisconnected && !game.TimeControlEnabled && now - lastActivity > AbandonedUntimedLimit)
            {
                game.ForceEnd("abandoned");
                await flow.BroadcastState(game);
                log.LogInformation("Game {Id} ended as abandoned by cleanup", game.SessionId);
            }
            else if (bothDisconnected && game.LastMoveTimestamp == null && now - game.CreatedAt > NoMoveLimit)
            {
                sessions.Remove(game.SessionId);
            }
            else if (bothDisconnected && now - lastActivity > StaleStartedLimit)
            {
                sessions.Remove(game.SessionId);
            }
        }

        // Forget games that are gone
        var present = games.Select(g => g.SessionId).ToHashSet();
        foreach (var id in _endedSeenAt.Keys.Where(id => !present.Contains(id)).ToList())
            _endedSeenAt.Remove(id);
    }

    /// <summary>True when the active player's clock ran out more than the grace period ago.</summary>
    private static bool FlagFallen(ServerGame game, DateTime now)
    {
        if (!game.TimeControlEnabled || !game.LastMoveTimestamp.HasValue) return false;
        if (game.Bidding != null && !game.Bidding.BothBid) return false;   // bid clock, not game clock
        var elapsedMs = (now - game.LastMoveTimestamp.Value).TotalMilliseconds;
        return game.ActiveColor.TimeMiliseconds - elapsedMs < -FlagGraceMs;
    }

    private async Task SweepOrphanedSessionsAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoff = DateTime.UtcNow - OrphanRowMinAge;
        var candidates = await db.GameSessions
            .Where(s => s.Result == GameResult.InProgress && s.StartedAt < cutoff)
            .Select(s => s.Id)
            .ToListAsync();
        var orphans = candidates.Where(id => sessions.GetById(id) == null).ToList();
        if (orphans.Count == 0) return;

        int deleted = 0;
        foreach (var chunk in orphans.Chunk(500))
            deleted += await db.GameSessions.Where(s => chunk.Contains(s.Id)).ExecuteDeleteAsync();
        log.LogInformation("Deleted {Count} orphaned in-progress game sessions", deleted);
    }
}
