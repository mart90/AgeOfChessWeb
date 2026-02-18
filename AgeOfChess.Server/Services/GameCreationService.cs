using System.Text.Json;
using AgeOfChess.Server.Data;
using AgeOfChess.Server.Data.Models;
using AgeOfChess.Server.GameLogic;

namespace AgeOfChess.Server.Services;

/// <summary>
/// Centralises the creation of a GameSession + ServerGame pair so that
/// all entry points (lobby creation, matchmaking, future tournament etc.)
/// go through a single code path.
/// </summary>
public class GameCreationService(AppDbContext db, GameSessionManager sessions)
{
    /// <param name="UserId">Null for anonymous players.</param>
    /// <param name="Elo">Current rating. Null for anonymous players.</param>
    /// <param name="DisplayName">Shown in-game. Falls back to "Player 1"/"Player 2".</param>
    public record PlayerInfo(int? UserId = null, int? Elo = null, string? DisplayName = null);

    /// <summary>
    /// Creates, persists, and registers a new game.
    /// </summary>
    public async Task<(GameSession Session, ServerGame Game)> CreateAsync(
        GameSettings settings,
        PlayerInfo? white = null,
        PlayerInfo? black = null,
        bool isPrivate = false)
    {
        white ??= new PlayerInfo();
        black ??= new PlayerInfo();

        var session = new GameSession
        {
            SettingsJson         = JsonSerializer.Serialize(settings),
            WhiteUserId          = white.UserId,
            BlackUserId          = black.UserId,
            BoardSize            = settings.BoardSize,
            TimeControlEnabled   = settings.TimeControlEnabled,
            StartTimeMinutes     = settings.StartTimeMinutes,
            TimeIncrementSeconds = settings.TimeIncrementSeconds,
            IsPrivate            = isPrivate,
        };
        db.GameSessions.Add(session);
        await db.SaveChangesAsync();   // get session.Id

        var game = new ServerGame(
            session.Id, settings,
            session.WhitePlayerToken, session.BlackPlayerToken,
            white.DisplayName ?? "Player 1",
            black.DisplayName ?? "Player 2");

        session.MapSeed  = game.GetMap().Seed;
        game.WhiteUserId = white.UserId;
        game.BlackUserId = black.UserId;
        game.WhiteElo    = white.Elo;
        game.BlackElo    = black.Elo;

        await db.SaveChangesAsync();
        sessions.Add(game);

        return (session, game);
    }
}
