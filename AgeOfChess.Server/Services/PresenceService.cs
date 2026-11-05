namespace AgeOfChess.Server.Services;

/// <summary>
/// Which signed-in users are on the site: a user is online while at least one of their tabs is
/// connected to the game hub (signed-in tabs stay connected on every page).
/// </summary>
public class PresenceService
{
    /// <summary>Offline for less than this counts as online (a page reload, a quick reconnect).</summary>
    public static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private readonly Dictionary<int, int> _connections = new();        // user → open connections
    private readonly Dictionary<int, DateTime> _offlineSince = new();  // user → last connection closed
    private readonly DateTime _startedAt = DateTime.UtcNow;            // users not seen since: offline since then

    public void Connected(int userId)
    {
        lock (_lock)
        {
            _connections[userId] = _connections.GetValueOrDefault(userId) + 1;
            _offlineSince.Remove(userId);
        }
    }

    public void Disconnected(int userId)
    {
        lock (_lock)
        {
            int left = _connections.GetValueOrDefault(userId) - 1;
            if (left > 0) { _connections[userId] = left; return; }
            _connections.Remove(userId);
            _offlineSince[userId] = DateTime.UtcNow;
        }
    }

    /// <summary>How long the user has had no connection (zero while connected).</summary>
    public TimeSpan OfflineFor(int userId)
    {
        lock (_lock)
        {
            if (_connections.ContainsKey(userId)) return TimeSpan.Zero;
            return DateTime.UtcNow - _offlineSince.GetValueOrDefault(userId, _startedAt);
        }
    }

    public bool IsOnline(int userId) => OfflineFor(userId) < ReconnectGrace;
}
