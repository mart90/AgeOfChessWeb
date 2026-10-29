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
///   OfferDraw(playerToken), AnswerDraw(playerToken, accept)                   — human games
///   AgreeComputerDraw(playerToken)                                             — vs-computer games
///   SubmitComputerMove(playerToken, type, fromX, fromY, toX, toY, pieceCode)  — vs-computer games
///   SubmitComputerBid(playerToken, amount)                                     — vs-computer games
///
/// Server → Client (broadcast to both players via group):
///   GameStarted(GameStateDto)   — when both players are connected
///   StateUpdated(GameStateDto)  — after every valid move/placement
///   GameEnded(GameStateDto)     — final state once the game is over
///   Error(string)               — sent only to the caller on invalid actions
/// </summary>
public class GameHub(GameSessionManager sessions, IServiceScopeFactory scopeFactory, GameFlowService flow) : Hub
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
        using var _ = await game.LockAsync();

        await Groups.AddToGroupAsync(Context.ConnectionId, game.GroupName);

        // Send the current state immediately so the spectator sees the live board
        await Clients.Caller.SendAsync("GameStarted", GameStateDtoBuilder.Build(game));

        if (game.ChatMessages.Count > 0)
            await Clients.Caller.SendAsync("ChatHistory", game.ChatMessages);
    }

    public async Task JoinGame(string playerToken)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null)
        {
            await Clients.Caller.SendAsync("Error", "Game not found.");
            return;
        }
        using var _ = await game.LockAsync();

        // Track this token on the connection so OnDisconnectedAsync can find it
        Context.Items["playerToken"] = playerToken;

        bool isWhite = game.IsWhitePlayer(playerToken);

        // If this slot already has a registered user, the caller's JWT must match
        var callerUserId   = GetCurrentUserId();
        int? expectedUserId = isWhite ? game.WhiteUserId : game.BlackUserId;
        if (expectedUserId.HasValue && callerUserId != expectedUserId.Value)
        {
            await Clients.Caller.SendAsync("Error", "Unauthorized.");
            return;
        }

        // Set connection ID only after passing auth check
        if (isWhite)
            game.WhiteConnectionId = Context.ConnectionId;
        else
            game.BlackConnectionId = Context.ConnectionId;

        // Associate user ID and display name if the player connected with a valid JWT
        if (callerUserId.HasValue)
        {
            if (isWhite) game.WhiteUserId = callerUserId.Value;
            else         game.BlackUserId = callerUserId.Value;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.FindAsync(callerUserId.Value);
            if (user != null)
            {
                var cat = EloService.GetCategory(game.Settings);
                int elo = cat switch
                {
                    TimeControlCategory.Bullet => user.EloBullet,
                    TimeControlCategory.Blitz  => user.EloBlitz,
                    TimeControlCategory.Rapid  => user.EloRapid,
                    _                          => user.EloSlow,
                };
                if (isWhite) { game.White.PlayedByStr = user.EffectiveDisplayName; game.WhiteElo = elo; }
                else         { game.Black.PlayedByStr = user.EffectiveDisplayName; game.BlackElo = elo; }
            }
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, game.GroupName);

        if (game.ChatMessages.Count > 0)
            await Clients.Caller.SendAsync("ChatHistory", game.ChatMessages);

        if (game.SideReady(true) && game.SideReady(false))
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
                // Game already started - only send to the caller (who just reconnected).
                // The other player already has the state and doesn't need a notification.
                await Clients.Caller.SendAsync("GameStarted", GameStateDtoBuilder.Build(game));
                if (game.Bidding != null && !game.Bidding.BothBid)
                    await Clients.Caller.SendAsync("BiddingStarted", BiddingStateDtoBuilder.Build(game.Bidding));
            }
        }
        else if (game.HasGameStarted)
        {
            // Only one player connected, but game already started — send current state to caller
            // so they can see the board and make moves while waiting for opponent to reconnect
            await Clients.Caller.SendAsync("GameStarted", GameStateDtoBuilder.Build(game));
            if (game.Bidding != null && !game.Bidding.BothBid)
                await Clients.Caller.SendAsync("BiddingStarted", BiddingStateDtoBuilder.Build(game.Bidding));
        }
    }

    public async Task MakeMove(string playerToken, int fromX, int fromY, int toX, int toY)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null) return;
        using var _ = await game.LockAsync();
        if (game.GameEnded) return;

        if (!IsAuthorizedPlayer(game, playerToken))
        {
            await Clients.Caller.SendAsync("Error", "Unauthorized.");
            return;
        }

        if (game.Bidding != null && !game.Bidding.BothBid) return;

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

        // Execute premove if opponent has one set
        await TryExecutePremove(game);

        await flow.BroadcastState(game);
    }

    public async Task PlacePiece(string playerToken, int toX, int toY, string pieceCode)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null) return;
        using var _ = await game.LockAsync();
        if (game.GameEnded) return;

        if (!IsAuthorizedPlayer(game, playerToken))
        {
            await Clients.Caller.SendAsync("Error", "Unauthorized.");
            return;
        }

        if (game.Bidding != null && !game.Bidding.BothBid) return;

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

        // Execute premove if opponent has one set
        await TryExecutePremove(game);

        await flow.BroadcastState(game);
    }

    /// <summary>
    /// Submits the computer's move in a game against the computer. The engine runs in the human
    /// player's browser, so the human's token authorizes it; it must be the computer's turn.
    /// <paramref name="type"/> is "move" or "place" (placements use <paramref name="pieceCode"/>).
    /// </summary>
    public async Task SubmitComputerMove(string playerToken, string type, int fromX, int fromY, int toX, int toY, string? pieceCode)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null || !game.IsComputerGame) return;
        using var _ = await game.LockAsync();
        if (game.GameEnded) return;
        if (!IsAuthorizedPlayer(game, playerToken)) return;
        if (game.Bidding != null && !game.Bidding.BothBid) return;

        bool callerIsWhite = game.IsWhitePlayer(playerToken);
        if (game.IsComputerSide(callerIsWhite)) return;                 // caller must be the human
        if (!game.IsComputerSide(game.ActiveColor.IsWhite)) return;     // and it must be the computer's turn

        bool applied;
        if (type == "place")
        {
            var pieceType = PieceTypeFromCode(pieceCode ?? "");
            applied = pieceType != null && game.TryPlacePiece(toX, toY, pieceType);
        }
        else
        {
            applied = game.TryMovePiece(fromX, fromY, toX, toY);
        }

        if (!applied)
        {
            await Clients.Caller.SendAsync("Error", "Illegal computer move.");
            return;
        }

        game.EndTurn();
        game.StartNewTurn();

        // The human may have premoved
        await TryExecutePremove(game);

        await flow.BroadcastState(game);
    }

    /// <summary>
    /// Called by the client when it detects the active player's clock has reached zero.
    /// The server re-validates: if the active player is genuinely out of time it ends the game.
    /// </summary>
    public async Task ClaimTimeout(string playerToken)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null) return;
        using var _ = await game.LockAsync();
        if (game.GameEnded || !game.LastMoveTimestamp.HasValue || !game.TimeControlEnabled) return;

        if (!IsAuthorizedPlayer(game, playerToken)) return;

        var active  = game.ActiveColor;
        var elapsed = (int)(DateTime.UtcNow - game.LastMoveTimestamp.Value).TotalMilliseconds;

        // Allow a 2-second grace window to absorb clock drift / network latency
        if (active.TimeMiliseconds - elapsed > 2000) return;

        active.TimeMiliseconds = 0;
        game.ForceEnd(active.IsWhite ? "b+t" : "w+t");
        await flow.BroadcastState(game);
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

        using (await game.LockAsync())
        {
            if (!IsAuthorizedPlayer(game, playerToken)) return;
            if (!game.SubmitBid(playerToken, amount)) return;
            if (!await AnnounceBid(game)) return;
        }
        await StartAfterBidReveal(game);
    }

    /// <summary>
    /// Records the computer's bid in a game vs the computer with bidding. The bid is calculated
    /// in the human player's browser (engine analysis of the starting position).
    /// </summary>
    public async Task SubmitComputerBid(string playerToken, int amount)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game?.Bidding == null || !game.IsComputerGame) return;

        using (await game.LockAsync())
        {
            if (!IsAuthorizedPlayer(game, playerToken)) return;
            if (game.IsComputerSide(game.IsWhitePlayer(playerToken))) return;   // caller must be the human
            if (!game.SubmitBid(game.ComputerPlayerToken, amount)) return;
            if (!await AnnounceBid(game)) return;
        }
        await StartAfterBidReveal(game);
    }

    /// <summary>
    /// Broadcasts a placed bid (amounts revealed only after both have bid). Returns true once both
    /// players have bid. Caller holds the game lock.
    /// </summary>
    private async Task<bool> AnnounceBid(ServerGame game)
    {
        var bidding = game.Bidding!;
        await Clients.Group(game.GroupName)
            .SendAsync("BidPlaced", BiddingStateDtoBuilder.Build(bidding));
        if (!bidding.BothBid) return false;

        // Persist post-bidding state for slow games (tokens may have been swapped by ResolveBidding)
        if (game.IsSlowGame)
            await flow.PersistBiddingResultAsync(game);
        return true;
    }

    /// <summary>Lets clients show the revealed bids for 2 seconds, then starts the game.</summary>
    private async Task StartAfterBidReveal(ServerGame game)
    {
        // Not under the lock, so the game stays usable (disconnects, chat) during the reveal
        await Task.Delay(2000);

        using var _ = await game.LockAsync();
        // After SwapColors (if joiner won), WhiteConnectionId points to the actual white player.
        // The computer has no connection.
        if (game.WhiteConnectionId != null)
            await Clients.Client(game.WhiteConnectionId).SendAsync("ColorAssigned", true);
        if (game.BlackConnectionId != null)
            await Clients.Client(game.BlackConnectionId).SendAsync("ColorAssigned", false);
        await Clients.Group(game.GroupName)
            .SendAsync("GameStarted", GameStateDtoBuilder.Build(game));
    }

    public async Task Resign(string playerToken)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null) return;
        using var _ = await game.LockAsync();
        if (game.GameEnded) return;

        if (!IsAuthorizedPlayer(game, playerToken)) return;

        game.ForceEnd(game.IsWhitePlayer(playerToken) ? "b+r" : "w+r");

        await flow.BroadcastState(game);
    }

    /// <summary>
    /// Offers a draw to the opponent (human games). If the opponent already offered one, this
    /// accepts it. Broadcasts DrawOfferChanged("w"|"b"|null, declined) to the group.
    /// </summary>
    public async Task OfferDraw(string playerToken)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null || game.IsComputerGame) return;
        using var _ = await game.LockAsync();
        if (!CanActInPlay(game, playerToken)) return;

        bool isWhite = game.IsWhitePlayer(playerToken);
        if (game.DrawOfferByWhite == !isWhite)
        {
            game.ForceEnd("draw");
            await flow.BroadcastState(game);
            return;
        }
        if (!game.CanOfferDraw(isWhite)) return;

        game.OfferDraw(isWhite);
        await Clients.Group(game.GroupName).SendAsync("DrawOfferChanged", isWhite ? "w" : "b", false);
    }

    /// <summary>Accepts (accept = true) or declines the opponent's standing draw offer.</summary>
    public async Task AnswerDraw(string playerToken, bool accept)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null) return;
        using var _ = await game.LockAsync();
        if (!CanActInPlay(game, playerToken)) return;
        if (game.DrawOfferByWhite != !game.IsWhitePlayer(playerToken)) return;   // no offer from the opponent

        if (accept)
        {
            game.ForceEnd("draw");
            await flow.BroadcastState(game);
        }
        else
        {
            game.ClearDrawOffer();
            await Clients.Group(game.GroupName).SendAsync("DrawOfferChanged", null, true);
        }
    }

    /// <summary>
    /// Ends a game vs the computer as a draw. The human offered one and the computer (the engine
    /// in the human's browser, as for its moves) accepted it.
    /// </summary>
    public async Task AgreeComputerDraw(string playerToken)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null || !game.IsComputerGame) return;
        using var _ = await game.LockAsync();
        if (!CanActInPlay(game, playerToken)) return;
        if (game.IsComputerSide(game.IsWhitePlayer(playerToken))) return;   // caller must be the human

        game.ForceEnd("draw");
        await flow.BroadcastState(game);
    }

    /// <summary>The game is under way (started, bidding over, not ended) and the caller plays in it.</summary>
    private bool CanActInPlay(ServerGame game, string playerToken) =>
        !game.GameEnded && game.HasGameStarted
        && (game.Bidding == null || game.Bidding.BothBid)
        && IsAuthorizedPlayer(game, playerToken);

    public async Task SendChat(string playerToken, string message)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null) return;
        using var _ = await game.LockAsync();

        if (!IsAuthorizedPlayer(game, playerToken)) return;

        message = message.Trim();
        if (string.IsNullOrEmpty(message) || message.Length > 500) return;

        bool isWhite = game.IsWhitePlayer(playerToken);
        string senderName = isWhite ? game.White.PlayedByStr : game.Black.PlayedByStr;
        var msg = new ChatMessageDto(senderName, message, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        if (game.ChatMessages.Count >= 200)
            game.ChatMessages.RemoveAt(0);
        game.ChatMessages.Add(msg);

        await Clients.Group(game.GroupName).SendAsync("ChatMessage", msg);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue("playerToken", out var tokenObj) && tokenObj is string playerToken)
        {
            var game = sessions.GetByPlayerToken(playerToken);
            if (game != null)
            {
                using var _ = await game.LockAsync();
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
    public async Task<int[][]> GetLegalMoves(string playerToken, int fromX, int fromY)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null) return [];
        using var _ = await game.LockAsync();
        if (game.GameEnded) return [];
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
    public async Task<int[][]> GetLegalPlacements(string playerToken, string pieceCode)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null) return [];
        using var _ = await game.LockAsync();
        if (game.GameEnded) return [];
        if (game.IsWhitePlayer(playerToken) != game.ActiveColor.IsWhite) return [];

        var type = PieceTypeFromCode(pieceCode);
        if (type == null) return [];
        return game.FindLegalPlacements(type)
                   .Select(s => new[] { s.X, s.Y })
                   .ToArray();
    }

    /// <summary>
    /// Sets a premove for the calling player. The premove will be executed immediately
    /// after the opponent's next move if it remains legal at that time.
    /// </summary>
    public async Task SetPremove(string playerToken, string type, int fromX, int fromY, int toX, int toY, string? pieceCode)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null) return;
        using var _ = await game.LockAsync();
        if (game.GameEnded) return;

        if (!IsAuthorizedPlayer(game, playerToken)) return;

        bool isWhite = game.IsWhitePlayer(playerToken);

        var premoveData = new ServerGame.PremoveData(
            type.ToLower() == "move" ? ServerGame.PremoveType.Move : ServerGame.PremoveType.Place,
            fromX, fromY, toX, toY, pieceCode
        );

        if (isWhite)
            game.WhitePremove = premoveData;
        else
            game.BlackPremove = premoveData;
    }

    /// <summary>
    /// Clears the calling player's premove (e.g., if they make a manual move).
    /// </summary>
    public async Task ClearPremove(string playerToken)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null) return;
        using var _ = await game.LockAsync();

        if (!IsAuthorizedPlayer(game, playerToken)) return;

        bool isWhite = game.IsWhitePlayer(playerToken);

        if (isWhite)
            game.WhitePremove = null;
        else
            game.BlackPremove = null;
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

    /// <summary>
    /// Called by a player after the game ends to request a rematch.
    /// When both players request, a new game is created with the same settings.
    /// If bidding is disabled, colors are swapped for the rematch.
    /// If both players request the same map seed, the seed is preserved.
    /// </summary>
    public async Task RequestRematch(string playerToken, bool sameSeed)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null || !game.GameEnded) return;
        if (!IsAuthorizedPlayer(game, playerToken)) return;

        bool isWhite = game.IsWhitePlayer(playerToken);
        bool shouldCreate = game.RequestRematch(isWhite, sameSeed);

        // Notify both players that someone requested a rematch
        await Clients.Group(game.GroupName).SendAsync("RematchRequested", isWhite, sameSeed);

        if (!shouldCreate) return;

        // Get the actual map seed from the database if both players want same board
        string? actualMapSeed = null;
        if (game.BothWantSameSeed)
        {
            using var dbScope = scopeFactory.CreateScope();
            var db = dbScope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Try HistoricGames first (game likely ended and was moved there)
            var historic = await db.HistoricGames.FindAsync(game.SessionId);
            if (historic != null)
                actualMapSeed = historic.MapSeed;
            else
            {
                // Fallback to GameSessions if still there
                var session = await db.GameSessions.FindAsync(game.SessionId);
                if (session != null)
                    actualMapSeed = session.MapSeed;
            }
        }

        // Build new settings: same config, optionally same seed
        var rematchSettings = new GameSettings
        {
            BoardSize          = game.Settings.BoardSize,
            TimeControlEnabled = game.Settings.TimeControlEnabled,
            StartTimeMinutes   = game.Settings.StartTimeMinutes,
            TimeIncrementSeconds = game.Settings.TimeIncrementSeconds,
            BiddingEnabled     = game.Settings.BiddingEnabled,
            MapMode            = game.Settings.MapMode,
            MapSeed            = actualMapSeed,
            IsRanked           = game.Settings.IsRanked,
        };

        // Without bidding: swap colors. With bidding: same initial assignment (bidding will resolve).
        GameCreationService.PlayerInfo whiteInfo, blackInfo;
        if (!rematchSettings.BiddingEnabled)
        {
            whiteInfo = new GameCreationService.PlayerInfo(game.BlackUserId, game.BlackElo, game.Black.PlayedByStr);
            blackInfo = new GameCreationService.PlayerInfo(game.WhiteUserId, game.WhiteElo, game.White.PlayedByStr);
        }
        else
        {
            whiteInfo = new GameCreationService.PlayerInfo(game.WhiteUserId, game.WhiteElo, game.White.PlayedByStr);
            blackInfo = new GameCreationService.PlayerInfo(game.BlackUserId, game.BlackElo, game.Black.PlayedByStr);
        }

        using var scope = scopeFactory.CreateScope();
        var creationService = scope.ServiceProvider.GetRequiredService<GameCreationService>();
        var (_, newGame) = await creationService.CreateAsync(rematchSettings, whiteInfo, blackInfo, isPrivate: game.IsPrivate);

        // Without bidding: old White → new Black, old Black → new White
        // With bidding:    old White → new White, old Black → new Black (colors TBD by bid)
        bool biddingEnabled = rematchSettings.BiddingEnabled;
        string oldWhiteToken = biddingEnabled ? newGame.WhitePlayerToken : newGame.BlackPlayerToken;
        string oldBlackToken = biddingEnabled ? newGame.BlackPlayerToken : newGame.WhitePlayerToken;
        bool   oldWhiteIsNewWhite = biddingEnabled;   // with bidding, initial; may change after bid
        bool   oldBlackIsNewWhite = !biddingEnabled;  // without bidding: old Black becomes new White

        if (game.WhiteConnectionId != null)
            await Clients.Client(game.WhiteConnectionId)
                .SendAsync("RematchReady", newGame.SessionId, oldWhiteToken, oldWhiteIsNewWhite);

        if (game.BlackConnectionId != null)
            await Clients.Client(game.BlackConnectionId)
                .SendAsync("RematchReady", newGame.SessionId, oldBlackToken, oldBlackIsNewWhite);

        // Remove the old game now that the rematch has been created
        sessions.Remove(game.SessionId);
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

    /// <summary>
    /// Attempts to execute the premove set by the now-active player (whose turn just started).
    /// Returns true if a premove was executed, false if no premove or premove was illegal.
    /// Caller holds the game lock.
    /// </summary>
    private async Task<bool> TryExecutePremove(ServerGame game)
    {
        if (game.GameEnded) return false;

        // Get the premove for the player who just became active
        var premove = game.ActiveColor.IsWhite ? game.WhitePremove : game.BlackPremove;

        if (premove == null) return false;

        // Clear the premove immediately (regardless of whether execution succeeds)
        if (game.ActiveColor.IsWhite)
            game.WhitePremove = null;
        else
            game.BlackPremove = null;

        bool success = false;
        // Position after the opponent's move, sent before the premove's position when the premove
        // executes: clients keep one state per move (move list navigation, post-game analysis).
        var stateBeforePremove = GameStateDtoBuilder.Build(game);

        try
        {
            if (premove.Type == ServerGame.PremoveType.Move)
            {
                // Attempt to execute the move
                if (game.TryMovePiece(premove.FromX, premove.FromY, premove.ToX, premove.ToY))
                {
                    success = true;
                }
            }
            else if (premove.Type == ServerGame.PremoveType.Place)
            {
                var pieceType = PieceTypeFromCode(premove.PieceCode!);
                if (pieceType != null && game.TryPlacePiece(premove.ToX, premove.ToY, pieceType))
                {
                    success = true;
                }
            }

            if (success)
            {
                // Complete the premove turn cycle
                game.EndTurn();
                game.StartNewTurn();
            }
        }
        catch
        {
            // If anything goes wrong, silently fail (premove becomes illegal, just discard it)
            success = false;
        }

        if (success)
            await Clients.Group(game.GroupName).SendAsync("StateUpdated", stateBeforePremove);

        return success;
    }
}
