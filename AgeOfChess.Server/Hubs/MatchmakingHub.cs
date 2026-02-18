using System.Security.Claims;
using AgeOfChess.Server.Data;
using AgeOfChess.Server.GameLogic;
using AgeOfChess.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace AgeOfChess.Server.Hubs;

/// <summary>
/// SignalR hub for matchmaking.
///
/// Client → Server:
///   JoinQueue(boardSizeMin, boardSizeMax, timeControlEnabled, startTimeMinutes, timeIncrementSeconds, timePref, biddingPreference)
///     timePref: "blitz"|"rapid"|"slow"|"any"  — category flexibility for matching
///   LeaveQueue()
///
/// Server → Client:
///   QueueCount(int)          — broadcast after every queue change
///   MatchFound({ gameId, playerToken })  — sent to each matched player
/// </summary>
public class MatchmakingHub(MatchmakingService matchmaking, IServiceScopeFactory scopeFactory) : Hub
{
    public async Task JoinQueue(
        int boardSizeMin,
        int boardSizeMax,
        bool timeControlEnabled,
        int startTimeMinutes,
        int timeIncrementSeconds,
        string timePref,
        string biddingPreference,
        string mapModePref = "m")
    {
        // Require authentication
        if (GetCurrentUserId() == null)
        {
            await Clients.Caller.SendAsync("Error", "You must be logged in to use matchmaking.");
            return;
        }

        // Send the current count immediately so the UI has something to show
        await Clients.Caller.SendAsync("QueueCount", matchmaking.QueueCount);

        var settings = new GameSettings
        {
            TimeControlEnabled   = timeControlEnabled,
            StartTimeMinutes     = startTimeMinutes,
            TimeIncrementSeconds = timeIncrementSeconds,
        };

        // "none" → player chose a specific time control button
        bool isSpecific = timePref.Equals("none", StringComparison.OrdinalIgnoreCase);

        // For category players: "any" → null (matches all); otherwise parse to enum
        TimeControlCategory? timeCategory = isSpecific || timePref.Equals("any", StringComparison.OrdinalIgnoreCase)
            ? null
            : Enum.TryParse<TimeControlCategory>(timePref, true, out var tc) ? tc : null;

        var biddingPref = Enum.TryParse<BiddingPreference>(biddingPreference, true, out var p)
            ? p
            : BiddingPreference.Either;

        var userId = GetCurrentUserId();
        int elo    = isSpecific
            ? await GetEloAsync(userId, settings)
            : await GetEloForCategoryAsync(userId, timeCategory);

        var normalizedMapMode = mapModePref.ToLowerInvariant() is "m" or "r" or "any"
            ? mapModePref.ToLowerInvariant()
            : "m";

        var entry = new MatchmakingEntry(
            Context.ConnectionId, userId, elo, settings, isSpecific, timeCategory, boardSizeMin, boardSizeMax, biddingPref, normalizedMapMode, DateTime.UtcNow);

        await matchmaking.AddToQueueAsync(entry);
    }

    public async Task LeaveQueue()
        => await matchmaking.RemoveFromQueueAsync(Context.ConnectionId);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await matchmaking.RemoveFromQueueAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private int? GetCurrentUserId()
    {
        var claim = Context.User?.FindFirst(ClaimTypes.NameIdentifier);
        return claim != null && int.TryParse(claim.Value, out var id) ? id : null;
    }

    private async Task<int> GetEloAsync(int? userId, GameSettings settings)
    {
        if (!userId.HasValue) return 1200;
        using var scope = scopeFactory.CreateScope();
        var db   = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FindAsync(userId.Value);
        if (user == null) return 1200;
        return EloService.GetCategory(settings) switch
        {
            TimeControlCategory.Blitz => user.EloBlitz,
            TimeControlCategory.Rapid => user.EloRapid,
            _                         => user.EloSlow,
        };
    }

    private async Task<int> GetEloForCategoryAsync(int? userId, TimeControlCategory? category)
    {
        if (!userId.HasValue) return 1200;
        using var scope = scopeFactory.CreateScope();
        var db   = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FindAsync(userId.Value);
        if (user == null) return 1200;
        return category switch
        {
            TimeControlCategory.Blitz => user.EloBlitz,
            TimeControlCategory.Rapid => user.EloRapid,
            TimeControlCategory.Slow  => user.EloSlow,
            _                         => user.EloRapid,  // "Any" → Rapid (default game is 10+5)
        };
    }
}
