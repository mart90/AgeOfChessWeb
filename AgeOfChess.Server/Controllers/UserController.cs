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
    public async Task<IActionResult> GetProfile(string username)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null) return NotFound();

        var sessions = await db.GameSessions
            .Where(g => (g.WhiteUserId == user.Id || g.BlackUserId == user.Id)
                        && g.Result != GameResult.InProgress)
            .OrderByDescending(g => g.EndedAt)
            .ToListAsync();

        // Batch-load opponents
        var opponentIds = sessions
            .Select(g => g.WhiteUserId == user.Id ? g.BlackUserId : g.WhiteUserId)
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();

        var opponents = (await db.Users
            .Where(u => opponentIds.Contains(u.Id)).ToListAsync())
            .ToDictionary(u => u.Id);

        var games = sessions.Select(g =>
        {
            bool isWhite = g.WhiteUserId == user.Id;
            var settings = JsonSerializer.Deserialize<GameSettings>(g.SettingsJson) ?? new GameSettings();
            var cat = EloService.GetCategory(settings);

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

            int moveCount = 0;
            try { moveCount = JsonSerializer.Deserialize<List<string>>(g.MovesJson)?.Count ?? 0; } catch { }

            return new
            {
                gameId               = g.Id,
                endedAt              = g.EndedAt,
                boardSize            = settings.BoardSize,
                timeControlEnabled   = settings.TimeControlEnabled,
                startTimeMinutes     = settings.StartTimeMinutes,
                timeIncrementSeconds = settings.TimeIncrementSeconds,
                category             = cat.ToString().ToLower(),
                opponentName,
                opponentUsername,
                opponentEloAtGame    = oppEloAtGame,
                myEloAtGame,
                eloDelta             = myEloDelta,
                result,
                resultDetail,
                moveCount,
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
        });
    }
}
