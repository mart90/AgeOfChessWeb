using System.Security.Claims;
using AgeOfChess.Server.Data;
using AgeOfChess.Server.Data.Models;
using AgeOfChess.Server.GameLogic;
using AgeOfChess.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
public class GameController(AppDbContext db, GameSessionManager sessions, GameCreationService gameCreation) : ControllerBase
{
    public record CreateGameRequest(
        int BoardSize = 12,
        bool TimeControlEnabled = false,
        int StartTimeMinutes = 10,
        int TimeIncrementSeconds = 5,
        bool BiddingEnabled = false,
        string? MapSeed = null
    );

    // POST /api/game
    [HttpPost]
    public async Task<IActionResult> CreateGame([FromBody] CreateGameRequest req)
    {
        var settings = new GameSettings
        {
            BoardSize            = req.BoardSize,
            TimeControlEnabled   = req.TimeControlEnabled,
            StartTimeMinutes     = req.StartTimeMinutes,
            TimeIncrementSeconds = req.TimeIncrementSeconds,
            BiddingEnabled       = req.BiddingEnabled,
            MapSeed              = req.MapSeed,
        };

        // Resolve creator info if authenticated
        GameCreationService.PlayerInfo white = new();
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (int.TryParse(userIdStr, out var creatorUserId))
        {
            var creator = await db.Users.FindAsync(creatorUserId);
            int? elo = null;
            if (creator != null)
            {
                var cat = EloService.GetCategory(settings);
                elo = cat switch
                {
                    TimeControlCategory.Blitz => creator.EloBlitz,
                    TimeControlCategory.Rapid => creator.EloRapid,
                    _                         => creator.EloSlow,
                };
            }
            white = new(creatorUserId, elo);
        }

        var (session, _) = await gameCreation.CreateAsync(settings, white);

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        return Ok(new
        {
            gameId           = session.Id,
            whitePlayerToken = session.WhitePlayerToken,
            inviteUrl        = $"{baseUrl}/game/{session.Id}?t={session.BlackPlayerToken}",
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

    // GET /api/game/live  — list of all in-progress games for the Watch page
    [HttpGet("live")]
    public async Task<IActionResult> GetLiveGames()
    {
        var games = sessions.GetAll().ToList();

        // Batch-fetch user records for any registered players
        var userIds = games
            .SelectMany(g => new[] { g.WhiteUserId, g.BlackUserId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var users = (await db.Users
            .Where(u => userIds.Contains(u.Id))
            .ToListAsync())
            .ToDictionary(u => u.Id);

        int? GetElo(int? userId, TimeControlCategory cat)
        {
            if (!userId.HasValue || !users.TryGetValue(userId.Value, out var u)) return null;
            return cat switch
            {
                TimeControlCategory.Blitz => u.EloBlitz,
                TimeControlCategory.Rapid => u.EloRapid,
                _                         => u.EloSlow,
            };
        }

        var list = games.Select(g =>
        {
            var cat = EloService.GetCategory(new GameSettings
            {
                TimeControlEnabled   = g.TimeControlEnabled,
                StartTimeMinutes     = g.StartTimeMinutes,
                TimeIncrementSeconds = g.TimeIncrementSeconds,
            });

            return new
            {
                id                   = g.SessionId,
                mapSeed              = g.GetMap().Seed,
                boardSize            = g.MapSize,
                timeControlEnabled   = g.TimeControlEnabled,
                startTimeMinutes     = g.StartTimeMinutes,
                timeIncrementSeconds = g.TimeIncrementSeconds,
                whiteName            = g.White.PlayedByStr,
                blackName            = g.Black.PlayedByStr,
                whiteElo             = GetElo(g.WhiteUserId, cat),
                blackElo             = GetElo(g.BlackUserId, cat),
            };
        }).ToList();

        return Ok(list);
    }

    // GET /api/game/my  — in-progress games for the current authenticated user
    [HttpGet("my")]
    [Authorize]
    public IActionResult GetMyGames()
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

        var list = sessions.GetAll()
            .Where(g => g.WhiteUserId == userId || g.BlackUserId == userId)
            .Select(g =>
            {
                bool isWhite = g.WhiteUserId == userId;
                var me  = isWhite ? g.White : g.Black;
                var opp = isWhite ? g.Black : g.White;
                return new
                {
                    id                   = g.SessionId,
                    isWhite,
                    opponentName         = opp.PlayedByStr,
                    isMyTurn             = me.IsActive,
                    timeControlEnabled   = g.TimeControlEnabled,
                    startTimeMinutes     = g.StartTimeMinutes,
                    timeIncrementSeconds = g.TimeIncrementSeconds,
                };
            })
            .ToList();

        return Ok(list);
    }

    // GET /api/game/{gameId}/token  — fetch own player token for reconnection on a new device
    [HttpGet("{gameId:int}/token")]
    [Authorize]
    public async Task<IActionResult> GetPlayerToken(int gameId)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

        var liveGame = sessions.GetById(gameId);
        if (liveGame == null) return NotFound();

        bool isWhite;
        if      (liveGame.WhiteUserId == userId) isWhite = true;
        else if (liveGame.BlackUserId == userId) isWhite = false;
        else return Forbid();

        var session = await db.GameSessions.FindAsync(gameId);
        if (session == null) return NotFound();

        var token = isWhite ? session.WhitePlayerToken : session.BlackPlayerToken;
        return Ok(new { playerToken = token, isWhite });
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
