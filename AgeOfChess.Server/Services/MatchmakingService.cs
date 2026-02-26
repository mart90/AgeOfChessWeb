using AgeOfChess.Server.Data;
using AgeOfChess.Server.GameLogic;
using AgeOfChess.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace AgeOfChess.Server.Services;

public enum BiddingPreference { Enabled, Disabled, Either }

public record MatchmakingEntry(
    string ConnectionId,
    int? UserId,
    int Elo,
    GameSettings Settings,          // specific TC settings (meaningful when IsSpecificTC = true)
    bool IsSpecificTC,              // true → player picked a specific time control button
    TimeControlCategory? TimePref,  // category when !IsSpecificTC; null = "Any"
    int BoardSizeMin,
    int BoardSizeMax,
    BiddingPreference BiddingPreference,
    string MapModePref,             // "m" | "r" | "any"
    DateTime QueuedAt);

/// <summary>
/// Singleton service that manages the matchmaking queue.
/// Tries to pair players on join, and again every 5 seconds (to trigger
/// the 30-second Elo-range expansion).
/// </summary>
public class MatchmakingService : IDisposable
{
    private readonly List<MatchmakingEntry> _queue = [];
    private readonly object _lock = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<MatchmakingHub> _hub;
    private readonly GameSessionManager _sessions;
    private readonly Timer _timer;

    public MatchmakingService(
        IServiceScopeFactory scopeFactory,
        IHubContext<MatchmakingHub> hub,
        GameSessionManager sessions)
    {
        _scopeFactory = scopeFactory;
        _hub          = hub;
        _sessions     = sessions;
        _timer        = new Timer(_ => TryMatchAllBackground(), null,
                            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public int QueueCount { get { lock (_lock) return _queue.Count; } }

    public async Task AddToQueueAsync(MatchmakingEntry entry)
    {
        List<(MatchmakingEntry, MatchmakingEntry)> pairs;
        lock (_lock)
        {
            _queue.Add(entry);
            pairs = FindMatchPairs();
            foreach (var (a, b) in pairs) { _queue.Remove(a); _queue.Remove(b); }
        }
        await ProcessMatchesAndBroadcastAsync(pairs);
    }

    public async Task RemoveFromQueueAsync(string connectionId)
    {
        bool removed;
        lock (_lock)
        {
            var entry = _queue.FirstOrDefault(e => e.ConnectionId == connectionId);
            removed = entry != null && _queue.Remove(entry);
        }
        if (removed)
            await BroadcastQueueCountAsync();
    }

    // ── Periodic background attempt ───────────────────────────────────────

    private async void TryMatchAllBackground()
    {
        try
        {
            List<(MatchmakingEntry, MatchmakingEntry)> pairs;
            lock (_lock)
            {
                pairs = FindMatchPairs();
                foreach (var (a, b) in pairs) { _queue.Remove(a); _queue.Remove(b); }
            }
            await ProcessMatchesAndBroadcastAsync(pairs);
        }
        catch { /* keep timer alive */ }
    }

    // ── Matching logic ────────────────────────────────────────────────────

    /// <summary>
    /// Must be called inside <see cref="_lock"/>.
    /// Returns pairs of compatible entries to match. Oldest entries are
    /// prioritised so long-waiting players get served first.
    /// </summary>
    private List<(MatchmakingEntry, MatchmakingEntry)> FindMatchPairs()
    {
        var sorted  = _queue.OrderBy(e => e.QueuedAt).ToList();
        var matched = new HashSet<string>();
        var pairs   = new List<(MatchmakingEntry, MatchmakingEntry)>();

        for (int i = 0; i < sorted.Count; i++)
        {
            var a = sorted[i];
            if (matched.Contains(a.ConnectionId)) continue;

            for (int j = i + 1; j < sorted.Count; j++)
            {
                var b = sorted[j];
                if (matched.Contains(b.ConnectionId)) continue;

                if (AreCompatible(a, b))
                {
                    pairs.Add((a, b));
                    matched.Add(a.ConnectionId);
                    matched.Add(b.ConnectionId);
                    break;
                }
            }
        }

        return pairs;
    }

    private static bool AreCompatible(MatchmakingEntry a, MatchmakingEntry b)
    {
        // Can't match a user with themselves (both logged in, same UserId)
        if (a.UserId.HasValue && b.UserId.HasValue && a.UserId.Value == b.UserId.Value) return false;

        // Board size ranges must overlap
        if (a.BoardSizeMax < b.BoardSizeMin || b.BoardSizeMax < a.BoardSizeMin) return false;

        // Effective matching category:
        //   specific-TC player → category derived from their settings
        //   category player    → TimePref (null = Any, accepts all)
        TimeControlCategory? catA = a.IsSpecificTC ? EloService.GetCategory(a.Settings) : a.TimePref;
        TimeControlCategory? catB = b.IsSpecificTC ? EloService.GetCategory(b.Settings) : b.TimePref;
        if (catA.HasValue && catB.HasValue && catA.Value != catB.Value) return false;

        // Enabled + Disabled is incompatible; all other bidding combos work
        if (a.BiddingPreference == BiddingPreference.Enabled  && b.BiddingPreference == BiddingPreference.Disabled) return false;
        if (a.BiddingPreference == BiddingPreference.Disabled && b.BiddingPreference == BiddingPreference.Enabled)  return false;

        // Map mode: "m"+"r" is incompatible; "any" matches either
        if (a.MapModePref != "any" && b.MapModePref != "any" && a.MapModePref != b.MapModePref) return false;

        // Elo range: ±200 if both waited < 30s; ±500 if either waited ≥ 30s
        var now              = DateTime.UtcNow;
        bool eitherLongWait = (now - a.QueuedAt).TotalSeconds >= 30 ||
                               (now - b.QueuedAt).TotalSeconds >= 30;
        int range = eitherLongWait ? 500 : 200;

        return Math.Abs(a.Elo - b.Elo) <= range;
    }

    // ── Game creation ─────────────────────────────────────────────────────

    private async Task ProcessMatchesAndBroadcastAsync(List<(MatchmakingEntry, MatchmakingEntry)> pairs)
    {
        foreach (var (a, b) in pairs)
            await CreateMatchAsync(a, b);

        await BroadcastQueueCountAsync();
    }

    private async Task CreateMatchAsync(MatchmakingEntry a, MatchmakingEntry b)
    {
        // Longer-waiting player gets white
        var white = a.QueuedAt <= b.QueuedAt ? a : b;
        var black = white == a ? b : a;

        // Disabled only if at least one player explicitly disabled it; Any+Any defaults to enabled
        bool biddingEnabled = white.BiddingPreference != BiddingPreference.Disabled &&
                              black.BiddingPreference != BiddingPreference.Disabled;

        // Resolve time control:
        //   specific TC player's settings beat category defaults;
        //   between two category players, the more specific (non-Any) preference wins.
        var (tcEnabled, tcStart, tcInc) = ResolveTimeControl(white, black);
        var settings = new GameSettings
        {
            BoardSize            = ResolveBoardSize(white.BoardSizeMin, white.BoardSizeMax, black.BoardSizeMin, black.BoardSizeMax),
            TimeControlEnabled   = tcEnabled,
            StartTimeMinutes     = tcStart,
            TimeIncrementSeconds = tcInc,
            BiddingEnabled       = biddingEnabled,
            MapMode              = ResolveMapMode(white.MapModePref, black.MapModePref),
        };

        using var scope = _scopeFactory.CreateScope();
        var db           = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gameCreation = scope.ServiceProvider.GetRequiredService<GameCreationService>();

        string whiteName = await GetDisplayNameAsync(db, white.UserId) ?? "Player 1";
        string blackName = await GetDisplayNameAsync(db, black.UserId) ?? "Player 2";

        var (session, _) = await gameCreation.CreateAsync(settings,
            new(white.UserId, white.UserId.HasValue ? white.Elo : null, whiteName),
            new(black.UserId, black.UserId.HasValue ? black.Elo : null, blackName),
            isPrivate: false,
            createdViaMatchmaking: true);

        await _hub.Clients.Client(white.ConnectionId)
            .SendAsync("MatchFound", new { gameId = session.Id, playerToken = session.WhitePlayerToken, isWhite = true });
        await _hub.Clients.Client(black.ConnectionId)
            .SendAsync("MatchFound", new { gameId = session.Id, playerToken = session.BlackPlayerToken, isWhite = false });
    }

    private static (bool enabled, int startMin, int incSec) ResolveTimeControl(MatchmakingEntry white, MatchmakingEntry black)
    {
        // A specific-TC selection always beats a category selection.
        if (white.IsSpecificTC)
            return (white.Settings.TimeControlEnabled, white.Settings.StartTimeMinutes, white.Settings.TimeIncrementSeconds);
        if (black.IsSpecificTC)
            return (black.Settings.TimeControlEnabled, black.Settings.StartTimeMinutes, black.Settings.TimeIncrementSeconds);

        // Both chose categories — prefer the more specific (non-Any) one; white wins ties.
        return CategoryDefault(white.TimePref ?? black.TimePref);
    }

    /// <summary>
    /// Default time control for each category when no specific TC was chosen.
    /// Bullet → 1+1 · Blitz → 5+3 · Rapid → 15+10 · Slow → 30+15 · Any → 10+5
    /// </summary>
    private static (bool enabled, int startMin, int incSec) CategoryDefault(TimeControlCategory? cat) =>
        cat switch
        {
            TimeControlCategory.Bullet => (true,  1,  1),
            TimeControlCategory.Blitz  => (true,  5,  3),
            TimeControlCategory.Rapid  => (true, 15, 10),
            TimeControlCategory.Slow   => (true, 30, 15),
            _                          => (true, 10,  5),  // Any → 10+5
        };

    /// <summary>
    /// Averages the four board-size range endpoints and rounds to the nearest
    /// valid (even) board size in [6, 20]. Banker's rounding means 10.5 → 10.
    /// </summary>
    private static int ResolveBoardSize(int minA, int maxA, int minB, int maxB)
    {
        double average = (minA + maxA + minB + maxB) / 4.0;
        int rounded = (int)(Math.Round(average / 2.0, MidpointRounding.ToEven) * 2);
        return Math.Clamp(rounded, 6, 20);
    }

    /// <summary>
    /// "any"+"any" → "m" (mirrored); "any"+specific → specific; same+same → same.
    /// </summary>
    private static string ResolveMapMode(string a, string b)
    {
        if (a == b) return a == "any" ? "m" : a;
        if (a == "any") return b;
        if (b == "any") return a;
        return "m"; // fallback
    }

    private static async Task<string?> GetDisplayNameAsync(AppDbContext db, int? userId)
    {
        if (!userId.HasValue) return null;
        var user = await db.Users.FindAsync(userId.Value);
        return user?.EffectiveDisplayName;
    }

    private async Task BroadcastQueueCountAsync()
    {
        int count;
        lock (_lock) count = _queue.Count;
        await _hub.Clients.All.SendAsync("QueueCount", count);
    }

    public void Dispose() => _timer.Dispose();
}
