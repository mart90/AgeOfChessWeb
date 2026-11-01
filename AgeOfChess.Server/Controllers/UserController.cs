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

        string? displayName = req.DisplayNameSameAsUsername || string.IsNullOrWhiteSpace(req.DisplayName)
            ? null
            : req.DisplayName!.Trim();
        if (AuthController.DisplayNameError(displayName) is { } error)
            return BadRequest(error);
        user.DisplayName = displayName;

        await db.SaveChangesAsync();
        return Ok(AuthController.MapUser(user));
    }

    private static string? ComputerOpponentName(HistoricGame g) =>
        g.Settings.ComputerLevel is int level ? ComputerLevels.Name(level) : null;

    /// <summary>Time-control category, as EloService.GetCategory, by name.</summary>
    private static string Category(bool timeControlEnabled, int startTimeMinutes) =>
        !timeControlEnabled || startTimeMinutes >= 30 ? "slow"
        : startTimeMinutes >= 10 ? "rapid"
        : startTimeMinutes >= 3  ? "blitz"
        :                          "bullet";

    /// <summary>The resolved handicap as "B +10" / "W +3" (automatic can be negative: "B −2"), or null.</summary>
    private static string? HandicapLabel(GameSettings s)
    {
        var (side, gold) = s.BlackHandicapGold != 0 ? ("B", s.BlackHandicapGold)
                         : s.WhiteHandicapGold != 0 ? ("W", s.WhiteHandicapGold)
                         : (null, 0);
        return side == null ? null : $"{side} {(gold > 0 ? "+" : "−")}{Math.Abs(gold)}";
    }

    // GET /api/user/{username}  — public profile
    [HttpGet("{username}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetProfile(
        string username,
        [FromQuery] int    startIndex = 0,
        [FromQuery] string sortCol    = "endedAt",
        [FromQuery] string sortDir    = "desc",
        [FromQuery] string category   = "all",
        [FromQuery] bool   unranked   = true,
        [FromQuery] string? opponent  = null,
        [FromQuery] bool   history    = true)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null) return NotFound();

        int userId = user.Id;

        // Base filter — SQL level (query HistoricGames instead of GameSessions)
        var baseQuery = db.HistoricGames
            .Where(g => (g.WhitePlayerId == userId || g.BlackPlayerId == userId)
                        && g.Result != null);

        // Rating history for the graph: every rated game, whatever the table shows. Per category
        // the rating before the first game, then after each game. Only on request (the page asks on
        // its first load; tabs, sorting and filters don't change it).
        Dictionary<string, List<int>>? ratingHistory = null;
        if (history)
        {
            var rated = await baseQuery
                .Where(g => (g.WhitePlayerId == userId ? g.WhiteEloDelta : g.BlackEloDelta) != null)
                .OrderBy(g => g.EndedAt)
                .Select(g => new
                {
                    g.Settings.TimeControlEnabled,
                    g.Settings.StartTimeMinutes,
                    Before = (g.WhitePlayerId == userId ? g.WhiteEloAtGame : g.BlackEloAtGame)!.Value,
                    Delta  = (g.WhitePlayerId == userId ? g.WhiteEloDelta  : g.BlackEloDelta)!.Value,
                })
                .ToListAsync();
            ratingHistory = new[] { "bullet", "blitz", "rapid", "slow" }.ToDictionary(c => c, _ => new List<int>());
            foreach (var r in rated)
            {
                var series = ratingHistory[Category(r.TimeControlEnabled, r.StartTimeMinutes)];
                if (series.Count == 0) series.Add(r.Before);
                series.Add(r.Before + r.Delta);
            }
        }

        if (!unranked)
            baseQuery = baseQuery.Where(g => g.Settings.IsRanked && g.Settings.ComputerLevel == null);

        // Opponent name filter: human opponents whose display name contains the text
        opponent = opponent?.Trim();
        if (!string.IsNullOrEmpty(opponent))
            baseQuery = baseQuery.Where(g => g.Settings.ComputerLevel == null
                && db.Users.Any(u => u.Id == (g.WhitePlayerId == userId ? g.BlackPlayerId : g.WhitePlayerId)
                                     && (u.DisplayName ?? u.Username).Contains(opponent)));

        // Games per category (with the filters above), for the tabs (the per-category counts on User are rated games only)
        var countRows = await baseQuery
            .GroupBy(g => !g.Settings.TimeControlEnabled || g.Settings.StartTimeMinutes >= 30 ? "slow"
                        : g.Settings.StartTimeMinutes >= 10 ? "rapid"
                        : g.Settings.StartTimeMinutes >= 3  ? "blitz"
                        :                                     "bullet")
            .Select(grp => new { Category = grp.Key, Count = grp.Count() })
            .ToListAsync();
        var gameCounts = new[] { "bullet", "blitz", "rapid", "slow" }
            .ToDictionary(c => c, c => countRows.FirstOrDefault(r => r.Category == c)?.Count ?? 0);
        gameCounts["all"] = countRows.Sum(r => r.Count);

        // Category filter — SQL level
        if (category != "all")
            baseQuery = category switch
            {
                "bullet" => baseQuery.Where(g => g.Settings.TimeControlEnabled && g.Settings.StartTimeMinutes < 3),
                "blitz"  => baseQuery.Where(g => g.Settings.TimeControlEnabled && g.Settings.StartTimeMinutes >= 3 && g.Settings.StartTimeMinutes < 10),
                "rapid"  => baseQuery.Where(g => g.Settings.TimeControlEnabled && g.Settings.StartTimeMinutes >= 10 && g.Settings.StartTimeMinutes < 30),
                _        => baseQuery.Where(g => !g.Settings.TimeControlEnabled || g.Settings.StartTimeMinutes >= 30),
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
                if (ComputerOpponentName(g) is { } computerName) return computerName;
                int? oppId = g.WhitePlayerId == userId ? g.BlackPlayerId : g.WhitePlayerId;
                return oppId.HasValue && opponents.TryGetValue(oppId.Value, out var o)
                    ? o.EffectiveDisplayName : "Anonymous";
            }

            games = (asc ? allGames.OrderBy(OppName) : allGames.OrderByDescending(OppName))
                .Skip(startIndex).Take(50).ToList();
        }
        else
        {
            // All other sorts — SQL level
            totalGames = await baseQuery.CountAsync();

            var sortedQuery = sortCol switch
            {
                "result"  => asc ? baseQuery.OrderBy(g => g.Result)    : baseQuery.OrderByDescending(g => g.Result),
                // Rating after the game (as shown); unrated games last either way
                "rating" => asc
                    ? baseQuery.OrderBy(g => (g.WhitePlayerId == userId ? g.WhiteEloAtGame + g.WhiteEloDelta : g.BlackEloAtGame + g.BlackEloDelta) == null)
                               .ThenBy(g => g.WhitePlayerId == userId ? g.WhiteEloAtGame + g.WhiteEloDelta : g.BlackEloAtGame + g.BlackEloDelta)
                               .ThenByDescending(g => g.EndedAt)
                    : baseQuery.OrderBy(g => (g.WhitePlayerId == userId ? g.WhiteEloAtGame + g.WhiteEloDelta : g.BlackEloAtGame + g.BlackEloDelta) == null)
                               .ThenByDescending(g => g.WhitePlayerId == userId ? g.WhiteEloAtGame + g.WhiteEloDelta : g.BlackEloAtGame + g.BlackEloDelta)
                               .ThenByDescending(g => g.EndedAt),
                "timeControl" => asc
                    ? baseQuery.OrderBy(g => g.Settings.TimeControlEnabled ? g.Settings.StartTimeMinutes * 60 + g.Settings.TimeIncrementSeconds : int.MaxValue)
                    : baseQuery.OrderByDescending(g => g.Settings.TimeControlEnabled ? g.Settings.StartTimeMinutes * 60 + g.Settings.TimeIncrementSeconds : int.MaxValue),
                "moveCount" => asc ? baseQuery.OrderBy(g => g.MoveCount)  : baseQuery.OrderByDescending(g => g.MoveCount),
                "boardSize" => asc ? baseQuery.OrderBy(g => g.Settings.BoardSize)  : baseQuery.OrderByDescending(g => g.Settings.BoardSize),
                // By size, whichever side got it
                "handicap"  => asc
                    ? baseQuery.OrderBy(g => Math.Abs(g.Settings.BlackHandicapGold) + Math.Abs(g.Settings.WhiteHandicapGold)).ThenByDescending(g => g.EndedAt)
                    : baseQuery.OrderByDescending(g => Math.Abs(g.Settings.BlackHandicapGold) + Math.Abs(g.Settings.WhiteHandicapGold)).ThenByDescending(g => g.EndedAt),
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

            var gs = g.Settings;
            bool vsComputer = gs.ComputerLevel != null;

            int? opponentId = isWhite ? g.BlackPlayerId : g.WhitePlayerId;
            User? opp = !vsComputer && opponentId.HasValue && opponents.TryGetValue(opponentId.Value, out var o) ? o : null;
            string  opponentName     = vsComputer ? ComputerLevels.Name(gs.ComputerLevel!.Value) : opp?.EffectiveDisplayName ?? "Anonymous";
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
                "draw"      => "½–½",
                "abandoned" => "Abandoned",
                _     => "—",
            };

            string cat = Category(gs.TimeControlEnabled, gs.StartTimeMinutes);

            return new
            {
                gameId               = g.Id,
                endedAt              = g.EndedAt,
                boardSize            = gs.BoardSize,
                mapMode              = gs.MapMode,
                timeControlEnabled   = gs.TimeControlEnabled,
                startTimeMinutes     = gs.StartTimeMinutes,
                timeIncrementSeconds = gs.TimeIncrementSeconds,
                category             = cat,
                opponentName,
                opponentUsername,
                opponentEloAtGame    = oppEloAtGame,
                myEloAtGame,
                eloDelta             = myEloDelta,
                result,
                resultDetail,
                moveCount            = g.MoveCount,
                isRanked             = gs.IsRanked && !vsComputer,
                vsComputer,
                handicap             = HandicapLabel(gs),
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
            gameCounts,
            ratingHistory,
        });
    }
}
