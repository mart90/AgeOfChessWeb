using System.Security.Claims;
using AgeOfChess.Server.Data;
using AgeOfChess.Server.Data.Models;
using AgeOfChess.Server.GameLogic;
using AgeOfChess.Server.GameLogic.Map;
using AgeOfChess.Server.GameLogic.PlaceableObjects;
using AgeOfChess.Server.GameLogic.PlaceableObjects.Pieces;
using AgeOfChess.Server.Services;
using Microsoft.AspNetCore.SignalR;
using System.Text.Json;

namespace AgeOfChess.Server.Hubs;

/// <summary>
/// Real-time hub for in-game communication. No JWT required — players are
/// identified by the opaque playerToken they received when the game was created.
///
/// Client → Server:
///   JoinGame(playerToken)
///   MakeMove(playerToken, fromX, fromY, toX, toY)
///   PlacePiece(playerToken, toX, toY, pieceCode)   "q"|"r"|"b"|"n"|"p"
///   Resign(playerToken)
///
/// Server → Client (broadcast to both players via group):
///   GameStarted(GameStateDto)   — when both players are connected
///   StateUpdated(GameStateDto)  — after every valid move/placement
///   GameEnded(GameStateDto)     — final state once the game is over
///   Error(string)               — sent only to the caller on invalid actions
/// </summary>
public class GameHub(GameSessionManager sessions, IServiceScopeFactory scopeFactory) : Hub
{
    /// <summary>
    /// Join as a read-only spectator. Spectators receive the same state broadcasts
    /// as players but cannot make moves.
    /// </summary>
    public async Task WatchGame(int sessionId)
    {
        var game = sessions.GetById(sessionId);
        if (game == null)
        {
            await Clients.Caller.SendAsync("Error", "Game not found.");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, game.GroupName);

        // Send the current state immediately so the spectator sees the live board
        await Clients.Caller.SendAsync("GameStarted", GameStateDtoBuilder.Build(game));
    }

    public async Task JoinGame(string playerToken)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null)
        {
            await Clients.Caller.SendAsync("Error", "Game not found.");
            return;
        }

        // Track this token on the connection so OnDisconnectedAsync can find it
        Context.Items["playerToken"] = playerToken;

        bool isWhite = game.IsWhitePlayer(playerToken);
        if (isWhite)
            game.WhiteConnectionId = Context.ConnectionId;
        else
            game.BlackConnectionId = Context.ConnectionId;

        // If this slot already has a registered user, the caller's JWT must match
        var callerUserId   = GetCurrentUserId();
        int? expectedUserId = isWhite ? game.WhiteUserId : game.BlackUserId;
        if (expectedUserId.HasValue && callerUserId != expectedUserId.Value)
        {
            await Clients.Caller.SendAsync("Error", "Unauthorized.");
            return;
        }

        // Associate user ID if the player connected with a valid JWT
        if (callerUserId.HasValue)
        {
            if (isWhite) game.WhiteUserId = callerUserId.Value;
            else         game.BlackUserId = callerUserId.Value;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, game.GroupName);

        if (game.WhiteConnected && game.BlackConnected)
        {
            if (!game.HasGameStarted)
            {
                game.HasGameStarted = true;
                if (game.BiddingEnabled)
                {
                    game.StartBidding();
                    // Send initial board so both clients can see the map while bidding
                    await Clients.Group(game.GroupName)
                        .SendAsync("GameStarted", GameStateDtoBuilder.Build(game));
                    await Clients.Group(game.GroupName)
                        .SendAsync("BiddingStarted", BiddingStateDtoBuilder.Build(game.Bidding!));
                }
                else
                {
                    await Clients.Group(game.GroupName)
                        .SendAsync("GameStarted", GameStateDtoBuilder.Build(game));
                }
            }
            else
            {
                // Player reconnecting — sync their client without disturbing the other player
                await Clients.Caller.SendAsync("GameStarted", GameStateDtoBuilder.Build(game));
                if (game.Bidding != null && !game.Bidding.BothBid)
                    await Clients.Caller.SendAsync("BiddingStarted", BiddingStateDtoBuilder.Build(game.Bidding));
            }
        }
    }

    public async Task MakeMove(string playerToken, int fromX, int fromY, int toX, int toY)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null || game.GameEnded) return;

        if (!IsAuthorizedPlayer(game, playerToken))
        {
            await Clients.Caller.SendAsync("Error", "Unauthorized.");
            return;
        }

        if (game.IsWhitePlayer(playerToken) != game.ActiveColor.IsWhite)
        {
            await Clients.Caller.SendAsync("Error", "It is not your turn.");
            return;
        }

        if (!game.TryMovePiece(fromX, fromY, toX, toY))
        {
            await Clients.Caller.SendAsync("Error", "Illegal move.");
            return;
        }

        game.EndTurn();
        game.StartNewTurn();

        await BroadcastState(game);
    }

    public async Task PlacePiece(string playerToken, int toX, int toY, string pieceCode)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null || game.GameEnded) return;

        if (!IsAuthorizedPlayer(game, playerToken))
        {
            await Clients.Caller.SendAsync("Error", "Unauthorized.");
            return;
        }

        if (game.IsWhitePlayer(playerToken) != game.ActiveColor.IsWhite)
        {
            await Clients.Caller.SendAsync("Error", "It is not your turn.");
            return;
        }

        var pieceType = PieceTypeFromCode(pieceCode);
        if (pieceType == null)
        {
            await Clients.Caller.SendAsync("Error", $"Unknown piece code: {pieceCode}");
            return;
        }

        if (!game.TryPlacePiece(toX, toY, pieceType))
        {
            await Clients.Caller.SendAsync("Error", "Illegal placement.");
            return;
        }

        game.EndTurn();
        game.StartNewTurn();

        await BroadcastState(game);
    }

    /// <summary>
    /// Called by the client when it detects the active player's clock has reached zero.
    /// The server re-validates: if the active player is genuinely out of time it ends the game.
    /// </summary>
    public async Task ClaimTimeout(string playerToken)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null || game.GameEnded || !game.LastMoveTimestamp.HasValue || !game.TimeControlEnabled) return;

        if (!IsAuthorizedPlayer(game, playerToken)) return;

        var active  = game.ActiveColor;
        var elapsed = (int)(DateTime.UtcNow - game.LastMoveTimestamp.Value).TotalMilliseconds;

        // Allow a 2-second grace window to absorb clock drift / network latency
        if (active.TimeMiliseconds - elapsed > 2000) return;

        active.TimeMiliseconds = 0;
        game.ForceEnd(active.IsWhite ? "b+t" : "w+t");
        await BroadcastState(game);
    }

    /// <summary>
    /// Called by a client to submit their bid during the bidding phase.
    /// When both players have bid the results are revealed, colors are assigned,
    /// and the game starts after a short delay.
    /// </summary>
    public async Task SubmitBid(string playerToken, int amount)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game?.Bidding == null) return;

        if (!IsAuthorizedPlayer(game, playerToken)) return;

        if (!game.SubmitBid(playerToken, amount)) return;

        // Broadcast bid placed (amounts revealed only after both have bid)
        await Clients.Group(game.GroupName)
            .SendAsync("BidPlaced", BiddingStateDtoBuilder.Build(game.Bidding));

        if (game.Bidding.BothBid)
        {
            // Let clients display the revealed bids for 2 seconds
            await Task.Delay(2000);

            // After SwapColors (if joiner won), WhiteConnectionId points to the actual white player
            await Clients.Client(game.WhiteConnectionId!).SendAsync("ColorAssigned", true);
            await Clients.Client(game.BlackConnectionId!).SendAsync("ColorAssigned", false);
            await Clients.Group(game.GroupName)
                .SendAsync("GameStarted", GameStateDtoBuilder.Build(game));
        }
    }

    public async Task Resign(string playerToken)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null || game.GameEnded) return;

        if (!IsAuthorizedPlayer(game, playerToken)) return;

        game.ForceEnd(game.IsWhitePlayer(playerToken) ? "b+r" : "w+r");

        await BroadcastState(game);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue("playerToken", out var tokenObj) && tokenObj is string playerToken)
        {
            var game = sessions.GetByPlayerToken(playerToken);
            if (game != null)
            {
                if (game.WhiteConnectionId == Context.ConnectionId)
                    game.WhiteConnectionId = null;
                else if (game.BlackConnectionId == Context.ConnectionId)
                    game.BlackConnectionId = null;
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Returns legal destination squares for the piece at (fromX, fromY).
    /// Called immediately when the player grabs a piece so the client can show green hints.
    /// </summary>
    public int[][] GetLegalMoves(string playerToken, int fromX, int fromY)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null || game.GameEnded) return [];
        if (game.IsWhitePlayer(playerToken) != game.ActiveColor.IsWhite) return [];

        var sq = game.GetMap().GetSquareByCoordinates(fromX, fromY);
        if (sq?.Object is not Piece piece) return [];

        return game.GetMap().FindLegalDestinationsForPiece(piece, sq)
                            .Select(s => new[] { s.X, s.Y })
                            .ToArray();
    }

    /// <summary>
    /// Returns legal placement squares for a given piece type.
    /// Called immediately when the player grabs a shop piece.
    /// </summary>
    public int[][] GetLegalPlacements(string playerToken, string pieceCode)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null || game.GameEnded) return [];
        if (game.IsWhitePlayer(playerToken) != game.ActiveColor.IsWhite) return [];

        var isPawn = pieceCode.Equals("p", StringComparison.OrdinalIgnoreCase);
        var pf = new PathFinder(game.GetMap());
        return pf.FindLegalDestinationsForPiecePlacement(game.ActiveColor.IsWhite, isPawn)
                 .Select(s => new[] { s.X, s.Y })
                 .ToArray();
    }

    // ── Private helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Verifies the caller is allowed to act as the player identified by
    /// <paramref name="playerToken"/>.
    ///
    /// Anonymous slots (no UserId set): the token alone is sufficient.
    /// Registered slots (UserId set): the caller's JWT must match the stored UserId.
    /// </summary>
    private bool IsAuthorizedPlayer(ServerGame game, string playerToken)
    {
        bool isWhite        = game.IsWhitePlayer(playerToken);
        int? expectedUserId = isWhite ? game.WhiteUserId : game.BlackUserId;

        if (!expectedUserId.HasValue)
            return true;   // anonymous slot — token alone is sufficient

        return GetCurrentUserId() == expectedUserId.Value;
    }

    private int? GetCurrentUserId()
    {
        var claim = Context.User?.FindFirst(ClaimTypes.NameIdentifier);
        return claim != null && int.TryParse(claim.Value, out var id) ? id : null;
    }

    private async Task BroadcastState(ServerGame game)
    {
        var state = GameStateDtoBuilder.Build(game);
        string eventName = game.GameEnded ? "GameEnded" : "StateUpdated";
        await Clients.Group(game.GroupName).SendAsync(eventName, state);

        if (game.GameEnded)
        {
            await PersistResult(game);
            sessions.Remove(game.SessionId);
        }
    }

    private async Task PersistResult(ServerGame game)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var session = await db.GameSessions.FindAsync(game.SessionId);
        if (session == null) return;

        session.Result    = ParseResult(game.Result);
        session.MovesJson = JsonSerializer.Serialize(game.MoveList.Select(m => m.ToNotation()));
        session.EndedAt   = DateTime.UtcNow;

        // Update Elo for both players if they were both authenticated
        await ApplyEloUpdates(db, game, session);

        await db.SaveChangesAsync();
    }

    private static async Task ApplyEloUpdates(AppDbContext db, ServerGame game, GameSession session)
    {
        if (!game.WhiteUserId.HasValue || !game.BlackUserId.HasValue) return;

        bool? whiteWon = game.Result switch
        {
            "w+c" or "w+g" or "w+s" or "w+t" or "w+r" => true,
            "b+c" or "b+g" or "b+s" or "b+t" or "b+r" => false,
            _ => null
        };
        if (!whiteWon.HasValue) return;

        var whiteUser = await db.Users.FindAsync(game.WhiteUserId.Value);
        var blackUser = await db.Users.FindAsync(game.BlackUserId.Value);
        if (whiteUser == null || blackUser == null) return;

        var settings = JsonSerializer.Deserialize<GameSettings>(session.SettingsJson)!;
        var category = EloService.GetCategory(settings);

        var (wElo, bElo) = category switch
        {
            TimeControlCategory.Blitz => (whiteUser.EloBlitz, blackUser.EloBlitz),
            TimeControlCategory.Rapid => (whiteUser.EloRapid, blackUser.EloRapid),
            _                         => (whiteUser.EloSlow,  blackUser.EloSlow),
        };

        var (wGames, bGames) = category switch
        {
            TimeControlCategory.Blitz => (whiteUser.BlitzGamesPlayed, blackUser.BlitzGamesPlayed),
            TimeControlCategory.Rapid => (whiteUser.RapidGamesPlayed, blackUser.RapidGamesPlayed),
            _                         => (whiteUser.SlowGamesPlayed,  blackUser.SlowGamesPlayed),
        };

        var (newWhite, newBlack) = EloService.Calculate(wElo, bElo, wGames, bGames, whiteWon.Value);

        // Snapshot pre-game ratings and deltas on the session record
        session.WhiteEloAtGame = wElo;
        session.BlackEloAtGame = bElo;
        session.WhiteEloDelta  = newWhite - wElo;
        session.BlackEloDelta  = newBlack - bElo;

        switch (category)
        {
            case TimeControlCategory.Blitz:
                whiteUser.EloBlitz = newWhite; whiteUser.BlitzGamesPlayed++;
                blackUser.EloBlitz = newBlack; blackUser.BlitzGamesPlayed++;
                break;
            case TimeControlCategory.Rapid:
                whiteUser.EloRapid = newWhite; whiteUser.RapidGamesPlayed++;
                blackUser.EloRapid = newBlack; blackUser.RapidGamesPlayed++;
                break;
            default:
                whiteUser.EloSlow = newWhite; whiteUser.SlowGamesPlayed++;
                blackUser.EloSlow = newBlack; blackUser.SlowGamesPlayed++;
                break;
        }

        whiteUser.GamesPlayed++;
        blackUser.GamesPlayed++;

        // Persist the actual (post-swap) user IDs into the session record
        session.WhiteUserId = game.WhiteUserId;
        session.BlackUserId = game.BlackUserId;
    }

    private static GameResult ParseResult(string? result) => result switch
    {
        "w+c" => GameResult.WhiteWinMate,
        "b+c" => GameResult.BlackWinMate,
        "w+g" => GameResult.WhiteWinGold,
        "b+g" => GameResult.BlackWinGold,
        "w+s" => GameResult.WhiteWinStalemate,
        "b+s" => GameResult.BlackWinStalemate,
        "w+t" => GameResult.WhiteWinTime,
        "b+t" => GameResult.BlackWinTime,
        "w+r" => GameResult.WhiteWinResign,
        "b+r" => GameResult.BlackWinResign,
        _ => GameResult.InProgress
    };

    private static Type? PieceTypeFromCode(string code) => code.ToLower() switch
    {
        "q" => typeof(GameLogic.PlaceableObjects.Pieces.Queen),
        "r" => typeof(GameLogic.PlaceableObjects.Pieces.Rook),
        "b" => typeof(GameLogic.PlaceableObjects.Pieces.Bishop),
        "n" => typeof(GameLogic.PlaceableObjects.Pieces.Knight),
        "p" => typeof(GameLogic.PlaceableObjects.Pieces.Pawn),
        _ => null
    };
}
