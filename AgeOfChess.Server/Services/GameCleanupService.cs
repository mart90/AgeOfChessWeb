namespace AgeOfChess.Server.Services;

/// <summary>
/// Background service that periodically evicts stale games from GameSessionManager.
///
/// Cleanup rules (all require both players to be disconnected):
///   • Never started (waiting for 2nd player, or both dropped before any move)
///       → remove after 2 hours from CreatedAt
///   • Active, timed game (TimeControlEnabled), no moves for 24 h
///       → remove (players can still reconnect to a server-restarted slow game via DB resume)
///   • Active, no time control (slow game), no moves for 7 days
///       → remove (generous window for asynchronous play)
///   • Ended game (safety net for the 10-minute fire-and-forget in BroadcastState)
///       → remove after 15 minutes
///
/// Games are only removed from the in-memory session store. The underlying
/// GameSession database record is NOT modified — abandoned live games will show
/// as InProgress in the DB, which is acceptable for this scale.
/// </summary>
public class GameCleanupService(GameSessionManager sessions) : BackgroundService
{
    private static readonly TimeSpan CheckInterval      = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan NeverStartedLimit  = TimeSpan.FromHours(2);
    private static readonly TimeSpan TimedGameLimit     = TimeSpan.FromHours(24);
    private static readonly TimeSpan SlowGameLimit      = TimeSpan.FromDays(7);
    private static readonly TimeSpan EndedGameLimit     = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger the first run so startup noise settles first.
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            Cleanup();
            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private void Cleanup()
    {
        var now = DateTime.UtcNow;

        foreach (var game in sessions.GetAll())
        {
            bool bothDisconnected = !game.WhiteConnected && !game.BlackConnected;

            if (game.GameEnded)
            {
                // Safety net: should have been removed by the fire-and-forget in BroadcastState.
                var lastActivity = game.LastMoveTimestamp ?? game.CreatedAt;
                if (now - lastActivity > EndedGameLimit)
                    sessions.Remove(game.SessionId);
            }
            else if (!game.HasGameStarted)
            {
                // Waiting for 2nd player (or both disconnected before game began).
                if (now - game.CreatedAt > NeverStartedLimit)
                    sessions.Remove(game.SessionId);
            }
            else if (bothDisconnected)
            {
                // Active game but both players have gone away.
                var lastActivity = game.LastMoveTimestamp ?? game.CreatedAt;
                var limit = game.TimeControlEnabled ? TimedGameLimit : SlowGameLimit;
                if (now - lastActivity > limit)
                    sessions.Remove(game.SessionId);
            }
            // If at least one player is connected, leave the game alone.
        }
    }
}
