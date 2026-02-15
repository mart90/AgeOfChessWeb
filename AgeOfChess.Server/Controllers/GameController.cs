using AgeOfChess.Server.Data;
using AgeOfChess.Server.Data.Models;
using AgeOfChess.Server.GameLogic;
using AgeOfChess.Server.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace AgeOfChess.Server.Controllers;

/// <summary>
/// Handles anonymous invite games (no account required).
///
/// Flow:
///   1. Host calls POST /api/game  → receives { gameId, whitePlayerToken, inviteUrl }
///   2. Host shares the inviteUrl with their friend
///   3. Friend calls POST /api/game/{gameId}/join  → receives { blackPlayerToken }
///   4. Both connect to SignalR hub and call JoinGame(playerToken)
///   5. Game starts when both are connected
///
/// GET /api/game/{gameId}  — returns current game state (for reconnect / replayer)
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class GameController(AppDbContext db, GameSessionManager sessions) : ControllerBase
{
    public record CreateGameRequest(
        int BoardSize = 12,
        bool TimeControlEnabled = false,
        int StartTimeMinutes = 10,
        int TimeIncrementSeconds = 5,
        string? MapSeed = null
    );

    // POST /api/game
    [HttpPost]
    public async Task<IActionResult> CreateGame([FromBody] CreateGameRequest req)
    {
        var settings = new GameSettings
        {
            BoardSize = req.BoardSize,
            TimeControlEnabled = req.TimeControlEnabled,
            StartTimeMinutes = req.StartTimeMinutes,
            TimeIncrementSeconds = req.TimeIncrementSeconds,
            MapSeed = req.MapSeed
        };

        var session = new GameSession
        {
            SettingsJson = JsonSerializer.Serialize(settings)
        };

        db.GameSessions.Add(session);
        await db.SaveChangesAsync();

        var game = new ServerGame(
            session.Id,
            settings,
            session.WhitePlayerToken,
            session.BlackPlayerToken,
            "Player 1",
            "Player 2"
        );

        session.MapSeed = game.GetMap().Seed;
        await db.SaveChangesAsync();

        sessions.Add(game);

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        return Ok(new
        {
            gameId = session.Id,
            whitePlayerToken = session.WhitePlayerToken,
            inviteUrl = $"{baseUrl}/game/{session.Id}?t={session.BlackPlayerToken}"
        });
    }

    // POST /api/game/{gameId}/join
    [HttpPost("{gameId:int}/join")]
    public async Task<IActionResult> JoinGame(int gameId)
    {
        var session = await db.GameSessions.FindAsync(gameId);
        if (session == null) return NotFound();

        // Game already has two players (i.e. it's in the session manager and both tokens are set)
        // The second player joins by connecting to SignalR with their token.
        // This endpoint just confirms the game exists and returns their token.
        return Ok(new
        {
            gameId = session.Id,
            blackPlayerToken = session.BlackPlayerToken,
            mapSeed = session.MapSeed
        });
    }

    // GET /api/game/{gameId}  — for reconnect and the replayer
    [HttpGet("{gameId:int}")]
    public async Task<IActionResult> GetGame(int gameId)
    {
        var session = await db.GameSessions.FindAsync(gameId);
        if (session == null) return NotFound();

        // If the game is still live, return current in-memory state
        var liveGame = sessions.GetById(gameId);
        if (liveGame != null)
            return Ok(GameStateDtoBuilder.Build(liveGame));

        // Game is over — return the stored state for replay
        return Ok(new
        {
            session.MapSeed,
            session.MovesJson,
            session.Result,
            session.StartedAt,
            session.EndedAt
        });
    }
}
