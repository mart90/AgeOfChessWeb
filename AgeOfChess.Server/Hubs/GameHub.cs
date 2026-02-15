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

        await Groups.AddToGroupAsync(Context.ConnectionId, game.GroupName);

        if (game.WhiteConnected && game.BlackConnected)
            await Clients.Group(game.GroupName).SendAsync("GameStarted", GameStateDtoBuilder.Build(game));
    }

    public async Task MakeMove(string playerToken, int fromX, int fromY, int toX, int toY)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null || game.GameEnded) return;

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

    public async Task Resign(string playerToken)
    {
        var game = sessions.GetByPlayerToken(playerToken);
        if (game == null || game.GameEnded) return;

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

        session.Result = ParseResult(game.Result);
        session.MovesJson = JsonSerializer.Serialize(game.MoveList.Select(m => m.ToNotation()));
        session.EndedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
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
