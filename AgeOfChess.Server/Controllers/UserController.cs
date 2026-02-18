using System.Security.Claims;
using System.Text.Json;
using AgeOfChess.Server.Data;
using AgeOfChess.Server.Data.Models;
using AgeOfChess.Server.GameLogic;
using AgeOfChess.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgeOfChess.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UserController(AppDbContext db) : ControllerBase
{
    public record UpdateSettingsRequest(string? DisplayName, bool DisplayNameSameAsUsername);

    // PUT /api/user/settings
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateSettingsRequest req)
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user   = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();

        user.DisplayName = req.DisplayNameSameAsUsername || string.IsNullOrWhiteSpace(req.DisplayName)
            ? null
            : req.DisplayName!.Trim();

        await db.SaveChangesAsync();
        return Ok(AuthController.MapUser(user));
    }

    // GET /api/user/{username}  — public profile
    [HttpGet("{username}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetProfile(
        string username,
        [FromQuery] int    startIndex = 0,
        [FromQuery] string sortCol    = "endedAt",
        [FromQuery] string sortDir    = "desc",
        [FromQuery] string category   = "all")
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null) return NotFound();

        int userId = user.Id;

        // Base filter — SQL level
        var baseQuery = db.GameSessions
            .Where(g => (g.WhiteUserId == userId || g.BlackUserId == userId)
                        && g.Result != GameResult.InProgress);

        // Category filter — SQL level using denormalized columns
        if (category != "all")
            baseQuery = category switch
            {
                "blitz" => baseQuery.Where(g => g.TimeControlEnabled && g.StartTimeMinutes < 10),
                "rapid" => baseQuery.Where(g => g.TimeControlEnabled && g.StartTimeMinutes >= 10 && g.StartTimeMinutes < 30),
                _       => baseQuery.Where(g => !g.TimeControlEnabled || g.StartTimeMinutes >= 30),
            };

        bool asc = sortDir == "asc";
        int totalGames;
        List<GameSession> sessions;
        Dictionary<int, User> opponents;

        if (sortCol == "opponent")
        {
            // Opponent name requires knowing both sides — sort in memory after loading all
            var allSessions = await baseQuery.ToListAsync();
            totalGames = allSessions.Count;

            var allOppIds = allSessions
                .Select(g => g.WhiteUserId == userId ? g.BlackUserId : g.WhiteUserId)
                .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
            opponents = (await db.Users.Where(u => allOppIds.Contains(u.Id)).ToListAsync())
                .ToDictionary(u => u.Id);

            string OppName(GameSession g)
            {
                int? oppId = g.WhiteUserId == userId ? g.BlackUserId : g.WhiteUserId;
                return oppId.HasValue && opponents.TryGetValue(oppId.Value, out var o)
                    ? o.EffectiveDisplayName : "Anonymous";
            }

            sessions = (asc ? allSessions.OrderBy(OppName) : allSessions.OrderByDescending(OppName))
                .Skip(startIndex).Take(50).ToList();
        }
        else
        {
            // All other sorts — SQL level using denormalized columns
            totalGames = await baseQuery.CountAsync();

            var sortedQuery = sortCol switch
            {
                "result"  => asc ? baseQuery.OrderBy(g => g.Result)    : baseQuery.OrderByDescending(g => g.Result),
                "eloDelta" => asc
                    ? baseQuery.OrderBy(g => g.WhiteUserId == userId ? g.WhiteEloDelta : g.BlackEloDelta)
                    : baseQuery.OrderByDescending(g => g.WhiteUserId == userId ? g.WhiteEloDelta : g.BlackEloDelta),
                "timeControl" => asc
                    ? baseQuery.OrderBy(g => g.TimeControlEnabled ? g.StartTimeMinutes * 60 + g.TimeIncrementSeconds : int.MaxValue)
                    : baseQuery.OrderByDescending(g => g.TimeControlEnabled ? g.StartTimeMinutes * 60 + g.TimeIncrementSeconds : int.MaxValue),
                "moveCount" => asc ? baseQuery.OrderBy(g => g.MoveCount)  : baseQuery.OrderByDescending(g => g.MoveCount),
                "boardSize" => asc ? baseQuery.OrderBy(g => g.BoardSize)  : baseQuery.OrderByDescending(g => g.BoardSize),
                _           => asc ? baseQuery.OrderBy(g => g.EndedAt)    : baseQuery.OrderByDescending(g => g.EndedAt),
            };

            sessions = await sortedQuery.Skip(startIndex).Take(50).ToListAsync();

            var oppIds = sessions
                .Select(g => g.WhiteUserId == userId ? g.BlackUserId : g.WhiteUserId)
                .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
            opponents = (await db.Users.Where(u => oppIds.Contains(u.Id)).ToListAsync())
                .ToDictionary(u => u.Id);
        }

        // Map the 10 sessions to response DTOs
        var games = sessions.Select(g =>
        {
            bool isWhite = g.WhiteUserId == userId;

            int? opponentId = isWhite ? g.BlackUserId : g.WhiteUserId;
            User? opp = opponentId.HasValue && opponents.TryGetValue(opponentId.Value, out var o) ? o : null;
            string  opponentName     = opp?.EffectiveDisplayName ?? "Anonymous";
            string? opponentUsername = opp?.Username;

            int? myEloAtGame  = isWhite ? g.WhiteEloAtGame : g.BlackEloAtGame;
            int? myEloDelta   = isWhite ? g.WhiteEloDelta  : g.BlackEloDelta;
            int? oppEloAtGame = isWhite ? g.BlackEloAtGame : g.WhiteEloAtGame;

            string result = g.Result switch
            {
                GameResult.WhiteWinMate or GameResult.WhiteWinGold or
                GameResult.WhiteWinStalemate or GameResult.WhiteWinTime or
                GameResult.WhiteWinResign   => isWhite ? "win"  : "loss",
                GameResult.BlackWinMate or GameResult.BlackWinGold or
                GameResult.BlackWinStalemate or GameResult.BlackWinTime or
                GameResult.BlackWinResign   => isWhite ? "loss" : "win",
                _                          => "draw",
            };

            string resultDetail = g.Result switch
            {
                GameResult.WhiteWinMate      => "W+#",
                GameResult.WhiteWinGold      => "W+G",
                GameResult.WhiteWinStalemate => "W+S",
                GameResult.WhiteWinTime      => "W+T",
                GameResult.WhiteWinResign    => "W+R",
                GameResult.BlackWinMate      => "B+#",
                GameResult.BlackWinGold      => "B+G",
                GameResult.BlackWinStalemate => "B+S",
                GameResult.BlackWinTime      => "B+T",
                GameResult.BlackWinResign    => "B+R",
                _                           => "—",
            };

            // Derive category from denormalized columns (no JSON parse needed)
            string cat = !g.TimeControlEnabled      ? "slow" :
                          g.StartTimeMinutes >= 30  ? "slow" :
                          g.StartTimeMinutes >= 10  ? "rapid" :
                                                      "blitz";

            // MapMode still requires the JSON blob (not a filter/sort column)
            string mapMode = "m";
            try
            {
                var settings = JsonSerializer.Deserialize<GameSettings>(g.SettingsJson);
                if (settings != null) mapMode = settings.MapMode;
            }
            catch { }

            return new
            {
                gameId               = g.Id,
                endedAt              = g.EndedAt,
                boardSize            = g.BoardSize,
                mapMode,
                timeControlEnabled   = g.TimeControlEnabled,
                startTimeMinutes     = g.StartTimeMinutes,
                timeIncrementSeconds = g.TimeIncrementSeconds,
                category             = cat,
                opponentName,
                opponentUsername,
                opponentEloAtGame    = oppEloAtGame,
                myEloAtGame,
                eloDelta             = myEloDelta,
                result,
                resultDetail,
                moveCount            = g.MoveCount,
            };
        }).ToList();

        return Ok(new
        {
            username    = user.Username,
            displayName = user.EffectiveDisplayName,
            stats = new
            {
                blitz = new { elo = user.EloBlitz, gamesPlayed = user.BlitzGamesPlayed },
                rapid = new { elo = user.EloRapid, gamesPlayed = user.RapidGamesPlayed },
                slow  = new { elo = user.EloSlow,  gamesPlayed = user.SlowGamesPlayed  },
            },
            games,
            totalGames,
        });
    }
}
