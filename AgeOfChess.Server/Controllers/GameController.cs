using System.Security.Claims;
using System.Text.Json;
using AgeOfChess.Server.Data;
using AgeOfChess.Server.Data.Models;
using AgeOfChess.Server.GameLogic;
using AgeOfChess.Server.GameLogic.PlaceableObjects.Pieces;
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
        string? MapSeed = null,
        string MapMode = "m",
        bool IsPrivate = false
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
            MapMode              = req.MapMode,
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
                    TimeControlCategory.Bullet => creator.EloBullet,
                    TimeControlCategory.Blitz  => creator.EloBlitz,
                    TimeControlCategory.Rapid  => creator.EloRapid,
                    _                          => creator.EloSlow,
                };
            }
            white = new(creatorUserId, elo, creator.EffectiveDisplayName);
        }

        var (session, _) = await gameCreation.CreateAsync(settings, white, isPrivate: req.IsPrivate);

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
                TimeControlCategory.Bullet => u.EloBullet,
                TimeControlCategory.Blitz  => u.EloBlitz,
                TimeControlCategory.Rapid  => u.EloRapid,
                _                          => u.EloSlow,
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
            .Where(g => !g.GameEnded && (g.WhiteUserId == userId || g.BlackUserId == userId))
            // Exclude matchmaking games that haven't started yet (not "your game" until both join)
            .Where(g => !g.CreatedViaMatchmaking || g.HasGameStarted)
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
                    waitingForOpponent   = !g.HasGameStarted,
                    timeControlEnabled   = g.TimeControlEnabled,
                    startTimeMinutes     = g.StartTimeMinutes,
                    timeIncrementSeconds = g.TimeIncrementSeconds,
                };
            })
            .ToList();

        return Ok(list);
    }

    // GET /api/game/open-lobbies  — public lobbies waiting for an opponent (logged-in users only)
    [HttpGet("open-lobbies")]
    [Authorize]
    public async Task<IActionResult> GetOpenLobbies()
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

        // In-memory games that haven't started yet, created by another logged-in user
        var waitingGames = sessions.GetAll()
            .Where(g => !g.HasGameStarted && !g.GameEnded
                        && g.WhiteUserId.HasValue && g.WhiteUserId != userId)
            .ToList();

        if (waitingGames.Count == 0) return Ok(Array.Empty<object>());

        // Filter out private lobbies; also fetch SettingsJson to check for custom map seeds
        var sessionIds = waitingGames.Select(g => g.SessionId).ToList();
        var dbRows = await db.GameSessions
            .Where(s => sessionIds.Contains(s.Id) && !s.IsPrivate)
            .Select(s => new { s.Id, s.SettingsJson })
            .ToListAsync();

        var publicIds    = dbRows.Select(r => r.Id).ToHashSet();
        var settingsById = dbRows.ToDictionary(r => r.Id, r => r.SettingsJson);

        var publicGames = waitingGames.Where(g => publicIds.Contains(g.SessionId)).ToList();
        if (publicGames.Count == 0) return Ok(Array.Empty<object>());

        // Fetch creator user records for Elo lookup
        var creatorIds = publicGames.Select(g => g.WhiteUserId!.Value).Distinct().ToList();
        var creators   = (await db.Users.Where(u => creatorIds.Contains(u.Id)).ToListAsync())
            .ToDictionary(u => u.Id);

        var list = publicGames.Select(g =>
        {
            var cat = EloService.GetCategory(new GameSettings
            {
                TimeControlEnabled   = g.TimeControlEnabled,
                StartTimeMinutes     = g.StartTimeMinutes,
                TimeIncrementSeconds = g.TimeIncrementSeconds,
            });

            creators.TryGetValue(g.WhiteUserId!.Value, out var creator);
            int? creatorElo = creator == null ? null : cat switch
            {
                TimeControlCategory.Blitz => creator.EloBlitz,
                TimeControlCategory.Rapid => creator.EloRapid,
                _                         => creator.EloSlow,
            };

            var map = g.GetMap();

            // Only expose the seed if the creator explicitly pasted one
            string? customSeed = null;
            if (settingsById.TryGetValue(g.SessionId, out var sJson))
            {
                var s = JsonSerializer.Deserialize<GameSettings>(sJson);
                if (s?.MapSeed != null) customSeed = map.Seed;
            }

            return new
            {
                id                   = g.SessionId,
                creatorName          = g.White.PlayedByStr,
                creatorElo,
                timeControlEnabled   = g.TimeControlEnabled,
                startTimeMinutes     = g.StartTimeMinutes,
                timeIncrementSeconds = g.TimeIncrementSeconds,
                boardSize            = g.MapSize,
                isFullRandom         = !map.IsMirrored,
                mapSeed              = customSeed,
            };
        }).ToList();

        return Ok(list);
    }

    // DELETE /api/game/{gameId}  — cancel an open challenge that hasn't started yet
    [HttpDelete("{gameId:int}")]
    [Authorize]
    public async Task<IActionResult> CancelChallenge(int gameId)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

        var game = sessions.GetById(gameId);
        if (game == null) return NotFound();
        if (game.HasGameStarted) return Conflict();
        if (game.WhiteUserId != userId) return Forbid();

        sessions.Remove(gameId);

        var session = await db.GameSessions.FindAsync(gameId);
        if (session != null)
        {
            db.GameSessions.Remove(session);
            await db.SaveChangesAsync();
        }

        return Ok();
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
        // Try GameSessions first (for live or recently ended games)
        var session = await db.GameSessions.FindAsync(gameId);
        if (session != null)
        {
            if (session.EndedAt == null) {
                // If the game is still live, return current in-memory state
                var liveGame = sessions.GetById(gameId);
                if (liveGame != null)
                    return Ok(GameStateDtoBuilder.Build(liveGame));

                // Game was in progress when the server restarted — in-memory state is lost
                if (session.Result == GameResult.InProgress)
                    return Ok(new { serverRestarted = true });
            }

            // Game is over
            return Ok(new
            {
                session.MapSeed,
                session.MovesJson,
                Result = session.Result.ToString(),
                session.StartedAt,
                session.EndedAt
            });
        }

        // Try HistoricGames (for finished games)
        var historicGame = await db.HistoricGames.FindAsync(gameId);
        if (historicGame == null) return NotFound();

        return Ok(new
        {
            historicGame.MapSeed,
            historicGame.MovesJson,
            historicGame.Result,
            StartedAt = historicGame.CreatedAt,
            historicGame.EndedAt
        });
    }

    // GET /api/game/{gameId}/replay  — full board snapshot sequence for replaying a finished game
    [HttpGet("{gameId:int}/replay")]
    public async Task<IActionResult> GetReplay(int gameId)
    {
        // Try GameSessions first
        var session = await db.GameSessions
            .Include(s => s.WhiteUser)
            .Include(s => s.BlackUser)
            .FirstOrDefaultAsync(s => s.Id == gameId);

        string mapSeed;
        string settingsJson;
        string movesJson;
        string? result;
        string whiteName;
        string blackName;
        int? blackStartingGold;
        string whitePlayerToken;
        string blackPlayerToken;

        if (session != null)
        {
            if (session.Result == GameResult.InProgress) return NotFound();
            if (string.IsNullOrEmpty(session.MapSeed)) return NotFound();

            mapSeed = session.MapSeed;
            settingsJson = session.SettingsJson;
            movesJson = session.MovesJson;
            result = session.Result.ToString();
            whiteName = session.WhiteUser?.EffectiveDisplayName ?? "Player 1";
            blackName = session.BlackUser?.EffectiveDisplayName ?? "Player 2";
            blackStartingGold = session.BlackStartingGold;
            whitePlayerToken = session.WhitePlayerToken;
            blackPlayerToken = session.BlackPlayerToken;
        }
        else
        {
            // Try HistoricGames
            var historicGame = await db.HistoricGames.FindAsync(gameId);
            if (historicGame == null) return NotFound();
            if (string.IsNullOrEmpty(historicGame.MapSeed)) return NotFound();

            mapSeed = historicGame.MapSeed;
            settingsJson = historicGame.SettingsJson;
            movesJson = historicGame.MovesJson;
            result = historicGame.Result;

            // Load user names manually
            User? whiteUser = historicGame.WhitePlayerId.HasValue
                ? await db.Users.FindAsync(historicGame.WhitePlayerId.Value)
                : null;
            User? blackUser = historicGame.BlackPlayerId.HasValue
                ? await db.Users.FindAsync(historicGame.BlackPlayerId.Value)
                : null;

            whiteName = whiteUser?.EffectiveDisplayName ?? "Player 1";
            blackName = blackUser?.EffectiveDisplayName ?? "Player 2";

            // Calculate BlackStartingGold from bids (winning bid goes to Black)
            if (historicGame.WhiteBid.HasValue && historicGame.BlackBid.HasValue)
                blackStartingGold = Math.Max(historicGame.WhiteBid.Value, historicGame.BlackBid.Value);
            else
                blackStartingGold = null;

            // HistoricGames don't store player tokens, generate dummy ones for replay
            whitePlayerToken = Guid.NewGuid().ToString("N");
            blackPlayerToken = Guid.NewGuid().ToString("N");
        }

        var settings = JsonSerializer.Deserialize<GameSettings>(settingsJson);
        if (settings == null) return StatusCode(500);

        settings.MapSeed = mapSeed;
        settings.TimeControlEnabled = false;

        var game = new ServerGame(
            gameId, settings,
            whitePlayerToken, blackPlayerToken,
            whiteName, blackName);

        if (blackStartingGold.HasValue)
            game.Black.Gold = blackStartingGold.Value;

        var snapshots = new List<GameStateDto>();
        snapshots.Add(GameStateDtoBuilder.Build(game));  // state before any moves

        var moveNotations = JsonSerializer.Deserialize<List<string>>(movesJson) ?? [];

        try
        {
            foreach (var notation in moveNotations)
            {
                var clean = notation.TrimEnd('+', '#');
                bool applied;

                if (clean.Contains('='))
                {
                    var eqIdx = clean.IndexOf('=');
                    var (toX, toY) = ParseSquare(clean[..eqIdx]);
                    var pieceType  = PieceTypeFromCode(clean[(eqIdx + 1)..].ToLower());
                    if (pieceType == null) return StatusCode(500);
                    applied = game.TryPlacePiece(toX, toY, pieceType, skipGoldCheck: true);
                }
                else
                {
                    char sep    = clean.Contains('x') ? 'x' : '-';
                    int  sepIdx = clean.IndexOf(sep);
                    var (fromX, fromY) = ParseSquare(clean[..sepIdx]);
                    var (toX,   toY)   = ParseSquare(clean[(sepIdx + 1)..]);
                    applied = game.TryMovePiece(fromX, fromY, toX, toY);
                }

                if (!applied)
                {
                    Console.Error.WriteLine($"[Replay {gameId}] Failed to apply move '{notation}' (move #{snapshots.Count})");
                    return StatusCode(500);
                }

                game.EndTurn();
                game.StartNewTurn();
                snapshots.Add(GameStateDtoBuilder.Build(game));

                if (game.GameEnded) break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Replay {gameId}] Exception at move #{snapshots.Count}: {ex}");
            return StatusCode(500);
        }

        // For resign/timeout games the result isn't captured in the move list —
        // force-end the game so the final snapshot has GameEnded = true.
        if (!game.GameEnded && result != null)
        {
            var resultStr = result switch
            {
                "WhiteWinResign" => "w+r",
                "BlackWinResign" => "b+r",
                "WhiteWinTime"   => "w+t",
                "BlackWinTime"   => "b+t",
                _ when result.StartsWith("w+") || result.StartsWith("b+") => result,
                _ => null
            };
            if (resultStr != null)
            {
                game.ForceEnd(resultStr);
                snapshots[^1] = GameStateDtoBuilder.Build(game);
            }
        }

        return Ok(new { snapshots });
    }

    private static (int x, int y) ParseSquare(string sq) =>
        (sq[0] - 'a', int.Parse(sq[1..]) - 1);

    private static Type? PieceTypeFromCode(string code) => code switch
    {
        "q" => typeof(Queen),
        "r" => typeof(Rook),
        "b" => typeof(Bishop),
        "n" => typeof(Knight),
        "p" => typeof(Pawn),
        _ => null
    };
}
