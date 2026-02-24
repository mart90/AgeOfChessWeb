using AgeOfChess.Server.GameLogic.Map;
using AgeOfChess.Server.Services;

namespace AgeOfChess.Server.GameLogic;

/// <summary>
/// Concrete Game subclass used by the server. Initialises the map and players
/// from GameSettings and holds the SignalR connection IDs for each player.
/// </summary>
public class ServerGame : Game
{
    public int SessionId { get; }
    public bool BiddingEnabled { get; }
    public bool IsSlowGame { get; }
    public string WhitePlayerToken { get; private set; }
    public string BlackPlayerToken { get; private set; }

    // SignalR connection IDs — set when each client connects to the hub
    public string? WhiteConnectionId { get; set; }
    public string? BlackConnectionId { get; set; }

    // Registered-user IDs — null for anonymous players. Swapped in SwapColors()
    // so they always track the actual in-game colour of each user.
    public int? WhiteUserId { get; set; }
    public int? BlackUserId { get; set; }

    // Elo ratings at the time the game started — null for anonymous / unrated players.
    public int? WhiteElo { get; set; }
    public int? BlackElo { get; set; }

    public bool WhiteConnected => WhiteConnectionId != null;
    public bool BlackConnected => BlackConnectionId != null;

    /// <summary>True once the initial GameStarted broadcast has been sent to both players.</summary>
    public bool HasGameStarted { get; set; }

    /// <summary>When this ServerGame was constructed — used by the cleanup service to evict stale sessions.</summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <summary>Original game settings — stored so a rematch can be created with the same config.</summary>
    public GameSettings Settings { get; private set; } = null!;

    /// <summary>In-memory chat history (not persisted, wiped on server restart).</summary>
    public List<ChatMessageDto> ChatMessages { get; } = new();

    // ── Rematch tracking ──────────────────────────────────────────────────

    private readonly Lock _rematchLock = new();
    public bool RematchRequestedByWhite { get; private set; }
    public bool RematchRequestedByBlack { get; private set; }
    public bool WhiteWantsSameSeed { get; private set; }
    public bool BlackWantsSameSeed { get; private set; }
    private bool _rematchCreated;

    /// <summary>
    /// Marks the given player as requesting a rematch.
    /// Returns true exactly once — when both players have requested — signalling
    /// that the caller should create the rematch game (race-condition safe).
    /// </summary>
    public bool RequestRematch(bool isWhite, bool sameSeed)
    {
        lock (_rematchLock)
        {
            if (_rematchCreated) return false;
            if (isWhite)
            {
                RematchRequestedByWhite = true;
                WhiteWantsSameSeed = sameSeed;
            }
            else
            {
                RematchRequestedByBlack = true;
                BlackWantsSameSeed = sameSeed;
            }

            if (RematchRequestedByWhite && RematchRequestedByBlack)
            {
                _rematchCreated = true;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Returns true if both players want to use the same map seed for the rematch.
    /// </summary>
    public bool BothWantSameSeed => WhiteWantsSameSeed && BlackWantsSameSeed;

    public string GroupName => $"game-{SessionId}";

    // ── Bidding ────────────────────────────────────────────────────────────

    /// <summary>Active bidding session, or null when bidding is not in progress.</summary>
    public BiddingSession? Bidding { get; private set; }

    private readonly int _startTimeMinutes;
    public int StartTimeMinutes => _startTimeMinutes;

    public ServerGame(int sessionId, GameSettings settings, string whitePlayerToken, string blackPlayerToken, string whitePlayerName, string blackPlayerName)
    {
        SessionId = sessionId;
        Settings = settings;
        BiddingEnabled = settings.BiddingEnabled;
        IsSlowGame = Services.EloService.GetCategory(settings) == Services.TimeControlCategory.Slow;
        WhitePlayerToken = whitePlayerToken;
        BlackPlayerToken = blackPlayerToken;
        _startTimeMinutes = settings.StartTimeMinutes;

        TimeControlEnabled = settings.TimeControlEnabled;
        TimeIncrementSeconds = settings.TimeIncrementSeconds;

        Colors = new List<PieceColor>
        {
            new PieceColor(true, whitePlayerName),
            new PieceColor(false, blackPlayerName)
        };

        // With bidding the gold start is determined by the bid outcome, not the fixed 10g default
        if (settings.BiddingEnabled)
            Black.Gold = 0;

        if (settings.TimeControlEnabled)
        {
            int baseMs = settings.StartTimeMinutes * 60 * 1000;
            // With bidding the clocks start at 110% to compensate for bid time spent;
            // they will be trimmed down by StartBidding once bidding begins.
            // Without bidding, use the plain base time.
            int startMs = (settings.BiddingEnabled && settings.TimeControlEnabled)
                ? (int)(baseMs * 1.1)
                : baseMs;
            White.TimeMiliseconds = startMs;
            Black.TimeMiliseconds = startMs;
        }

        var generator = new MapGenerator();
        Map = settings.MapSeed != null
            ? generator.GenerateFromSeed(settings.MapSeed)
            : settings.MapMode == "r"
                ? generator.GenerateFullRandom(settings.BoardSize, settings.BoardSize)
                : generator.GenerateMirrored(settings.BoardSize, settings.BoardSize);
    }

    // ── Bidding methods ───────────────────────────────────────────────────

    /// <summary>
    /// Called when both players have connected and bidding is enabled.
    /// Sets both players' clocks to startTime × 1.1 and opens the bidding window.
    /// </summary>
    public void StartBidding()
    {
        int initialMs = TimeControlEnabled
            ? (int)(_startTimeMinutes * 60 * 1000 * 1.1)
            : int.MaxValue;   // no clock when time control disabled

        Bidding = new BiddingSession { InitialMs = initialMs };

        if (TimeControlEnabled)
        {
            White.TimeMiliseconds = initialMs;
            Black.TimeMiliseconds = initialMs;
        }
    }

    /// <summary>
    /// Records a player's bid. Returns false if the bid is invalid (wrong phase, already bid).
    /// After both players bid, resolves colours and starts the game clock.
    /// </summary>
    public bool SubmitBid(string playerToken, int amount)
    {
        if (Bidding == null) return false;

        bool isCreator = playerToken == WhitePlayerToken;  // creator holds white token initially
        bool isJoiner  = playerToken == BlackPlayerToken;

        if (!isCreator && !isJoiner) return false;

        if (isCreator)
        {
            if (Bidding.CreatorBid.HasValue) return false;
            int elapsed = (int)(DateTime.UtcNow - Bidding.StartedAt).TotalMilliseconds;
            Bidding.CreatorFrozenMs = Math.Max(0, Bidding.InitialMs - elapsed);
            Bidding.CreatorBid = amount;
        }
        else
        {
            if (Bidding.JoinerBid.HasValue) return false;
            int elapsed = (int)(DateTime.UtcNow - Bidding.StartedAt).TotalMilliseconds;
            Bidding.JoinerFrozenMs = Math.Max(0, Bidding.InitialMs - elapsed);
            Bidding.JoinerBid = amount;
        }

        if (Bidding.BothBid)
            ResolveBidding();

        return true;
    }

    private void ResolveBidding()
    {
        var b = Bidding!;

        // Creator wins on tie (they created the game).
        bool joinerWins = b.JoinerBid!.Value > b.CreatorBid!.Value;

        int winnerFrozenMs = joinerWins ? b.JoinerFrozenMs : b.CreatorFrozenMs;
        int loserFrozenMs  = joinerWins ? b.CreatorFrozenMs : b.JoinerFrozenMs;
        int winningBid     = joinerWins ? b.JoinerBid.Value : b.CreatorBid.Value;

        if (joinerWins)
            SwapColors();

        // After potential swap, White is the bidding winner and Black is the loser.
        if (TimeControlEnabled)
        {
            White.TimeMiliseconds = winnerFrozenMs;
            Black.TimeMiliseconds = loserFrozenMs;
        }

        Black.Gold = winningBid;

        // Start white's game clock immediately.
        LastMoveTimestamp = DateTime.UtcNow;
    }

    /// <summary>
    /// Swaps white/black tokens and connection IDs so the joiner becomes white.
    /// The board is not modified — piece colours are determined by PieceColor.IsWhite.
    /// </summary>
    public void SwapColors()
    {
        (WhitePlayerToken, BlackPlayerToken) = (BlackPlayerToken, WhitePlayerToken);
        (WhiteConnectionId, BlackConnectionId) = (BlackConnectionId, WhiteConnectionId);
        (WhiteUserId, BlackUserId) = (BlackUserId, WhiteUserId);
        (WhiteElo, BlackElo) = (BlackElo, WhiteElo);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    public string? GetPlayerToken(string connectionId)
    {
        if (connectionId == WhiteConnectionId) return WhitePlayerToken;
        if (connectionId == BlackConnectionId) return BlackPlayerToken;
        return null;
    }

    public bool IsWhitePlayer(string playerToken) => playerToken == WhitePlayerToken;
}
