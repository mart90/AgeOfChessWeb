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

        // Base filter — SQL level (query HistoricGames instead of GameSessions)
        var baseQuery = db.HistoricGames
            .Where(g => (g.WhitePlayerId == userId || g.BlackPlayerId == userId)
                        && g.Result != null);

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
        List<HistoricGame> games;
        Dictionary<int, User> opponents;

        if (sortCol == "opponent")
        {
            // Opponent name requires knowing both sides — sort in memory after loading all
            var allGames = await baseQuery.ToListAsync();
            totalGames = allGames.Count;

            var allOppIds = allGames
                .Select(g => g.WhitePlayerId == userId ? g.BlackPlayerId : g.WhitePlayerId)
                .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
            opponents = (await db.Users.Where(u => allOppIds.Contains(u.Id)).ToListAsync())
                .ToDictionary(u => u.Id);

            string OppName(HistoricGame g)
            {
                int? oppId = g.WhitePlayerId == userId ? g.BlackPlayerId : g.WhitePlayerId;
                return oppId.HasValue && opponents.TryGetValue(oppId.Value, out var o)
                    ? o.EffectiveDisplayName : "Anonymous";
            }

            games = (asc ? allGames.OrderBy(OppName) : allGames.OrderByDescending(OppName))
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
                    ? baseQuery.OrderBy(g => g.WhitePlayerId == userId ? g.WhiteEloDelta : g.BlackEloDelta)
                    : baseQuery.OrderByDescending(g => g.WhitePlayerId == userId ? g.WhiteEloDelta : g.BlackEloDelta),
                "timeControl" => asc
                    ? baseQuery.OrderBy(g => g.TimeControlEnabled ? g.StartTimeMinutes * 60 + g.TimeIncrementSeconds : int.MaxValue)
                    : baseQuery.OrderByDescending(g => g.TimeControlEnabled ? g.StartTimeMinutes * 60 + g.TimeIncrementSeconds : int.MaxValue),
                "moveCount" => asc ? baseQuery.OrderBy(g => g.MoveCount)  : baseQuery.OrderByDescending(g => g.MoveCount),
                "boardSize" => asc ? baseQuery.OrderBy(g => g.BoardSize)  : baseQuery.OrderByDescending(g => g.BoardSize),
                _           => asc ? baseQuery.OrderBy(g => g.EndedAt)    : baseQuery.OrderByDescending(g => g.EndedAt),
            };

            games = await sortedQuery.Skip(startIndex).Take(50).ToListAsync();

            var oppIds = games
                .Select(g => g.WhitePlayerId == userId ? g.BlackPlayerId : g.WhitePlayerId)
                .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
            opponents = (await db.Users.Where(u => oppIds.Contains(u.Id)).ToListAsync())
                .ToDictionary(u => u.Id);
        }

        // Map the historic games to response DTOs
        var gamesResponse = games.Select(g =>
        {
            bool isWhite = g.WhitePlayerId == userId;

            int? opponentId = isWhite ? g.BlackPlayerId : g.WhitePlayerId;
            User? opp = opponentId.HasValue && opponents.TryGetValue(opponentId.Value, out var o) ? o : null;
            string  opponentName     = opp?.EffectiveDisplayName ?? "Anonymous";
            string? opponentUsername = opp?.Username;

            int? myEloAtGame  = isWhite ? g.WhiteEloAtGame : g.BlackEloAtGame;
            int? myEloDelta   = isWhite ? g.WhiteEloDelta  : g.BlackEloDelta;
            int? oppEloAtGame = isWhite ? g.BlackEloAtGame : g.WhiteEloAtGame;

            // HistoricGame.Result is a string like "w+c", "b+g", etc.
            bool whiteWon = g.Result?.StartsWith("w+") ?? false;
            bool blackWon = g.Result?.StartsWith("b+") ?? false;
            string result = whiteWon ? (isWhite ? "win" : "loss")
                          : blackWon ? (isWhite ? "loss" : "win")
                          : "draw";

            string resultDetail = g.Result switch
            {
                "w+c" => "W+#",
                "w+g" => "W+G",
                "w+s" => "W+S",
                "w+t" => "W+T",
                "w+r" => "W+R",
                "b+c" => "B+#",
                "b+g" => "B+G",
                "b+s" => "B+S",
                "b+t" => "B+T",
                "b+r" => "B+R",
                _     => "—",
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
                bullet = new { elo = user.EloBullet, gamesPlayed = user.BulletGamesPlayed },
                blitz  = new { elo = user.EloBlitz,  gamesPlayed = user.BlitzGamesPlayed  },
                rapid  = new { elo = user.EloRapid,  gamesPlayed = user.RapidGamesPlayed  },
                slow   = new { elo = user.EloSlow,   gamesPlayed = user.SlowGamesPlayed   },
            },
            games = gamesResponse,
            totalGames,
        });
    }
}
