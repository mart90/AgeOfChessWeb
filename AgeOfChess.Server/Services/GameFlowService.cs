using System.Text.Json;
using AgeOfChess.Server.Data;
using AgeOfChess.Server.Data.Models;
using AgeOfChess.Server.GameLogic;
using AgeOfChess.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace AgeOfChess.Server.Services;

/// <summary>
/// Broadcasting and persisting game state, shared by <see cref="GameHub"/> and background services
/// (e.g. <see cref="GameCleanupService"/> ending abandoned games), so a game ends the same way
/// whoever ends it.
/// </summary>
public class GameFlowService(IHubContext<GameHub> hub, GameSessionManager sessions, IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// Sends the game state to everyone in the game's group (GameEnded once the game is over) and
    /// persists it: the final result (history, Elo) or, for slow games, the move list.
    /// Caller holds the game lock.
    /// </summary>
    public async Task BroadcastState(ServerGame game)
    {
        var state = GameStateDtoBuilder.Build(game);
        string eventName = game.GameEnded ? "GameEnded" : "StateUpdated";
        await hub.Clients.Group(game.GroupName).SendAsync(eventName, state);

        if (game.GameEnded)
        {
            await PersistResult(game);
            // Keep the game in sessions briefly so players can request a rematch.
            // Fire-and-forget: remove after 10 minutes regardless of whether a rematch is created.
            // RequestRematch also calls sessions.Remove when a rematch starts; the double-remove is harmless.
            _ = Task.Delay(TimeSpan.FromMinutes(10))
                    .ContinueWith(_ => sessions.Remove(game.SessionId));
        }
        else if (game.IsSlowGame)
        {
            await PersistSlowGameProgressAsync(game);
        }
    }

    private async Task PersistSlowGameProgressAsync(ServerGame game)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.GameSessions.FindAsync(game.SessionId);
        if (session == null) return;

        session.MovesJson = JsonSerializer.Serialize(game.MoveList.Select(m => m.ToNotation()));
        session.MoveCount = game.MoveList.Count;
        if (game.TimeControlEnabled)
        {
            session.WhiteTimeMsRemaining = game.White.TimeMiliseconds;
            session.BlackTimeMsRemaining = game.Black.TimeMiliseconds;
        }

        await db.SaveChangesAsync();
    }

    public async Task PersistBiddingResultAsync(ServerGame game)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.GameSessions.FindAsync(game.SessionId);
        if (session == null) return;

        // Tokens and user IDs may have been swapped by ResolveBidding
        session.WhitePlayerToken = game.WhitePlayerToken;
        session.BlackPlayerToken = game.BlackPlayerToken;
        session.WhiteUserId      = game.WhiteUserId;
        session.BlackUserId      = game.BlackUserId;
        session.BlackStartingGold = game.Black.Gold;
        session.SettingsJson      = JsonSerializer.Serialize(game.Settings);   // ComputerIsWhite may have flipped
        if (game.TimeControlEnabled)
        {
            session.WhiteTimeMsRemaining = game.White.TimeMiliseconds;
            session.BlackTimeMsRemaining = game.Black.TimeMiliseconds;
        }

        await db.SaveChangesAsync();
    }

    private async Task PersistResult(ServerGame game)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var session = await db.GameSessions.FindAsync(game.SessionId);
        if (session == null) return;

        // Extract bid information if bidding was enabled
        int? whiteBid = null;
        int? blackBid = null;
        if (game.Bidding?.BothBid == true)
        {
            // Determine which bid belongs to which color
            // If joiner bid higher, they became White after the swap
            bool joinerWonBidding = game.Bidding.JoinerBid!.Value > game.Bidding.CreatorBid!.Value;
            whiteBid = joinerWonBidding ? game.Bidding.JoinerBid.Value : game.Bidding.CreatorBid.Value;
            blackBid = joinerWonBidding ? game.Bidding.CreatorBid.Value : game.Bidding.JoinerBid.Value;
        }

        // Update Elo for both players if they were both authenticated
        // This sets WhiteEloAtGame, BlackEloAtGame, WhiteEloDelta, BlackEloDelta on session
        await ApplyEloUpdates(db, game, session);

        // Create HistoricGame entry with all game data (preserve the same Id)
        var historicGame = new HistoricGame
        {
            Id = session.Id,
            WhitePlayerId = game.WhiteUserId,
            BlackPlayerId = game.BlackUserId,
            WhiteEloAtGame = session.WhiteEloAtGame,
            BlackEloAtGame = session.BlackEloAtGame,
            WhiteEloDelta = session.WhiteEloDelta,
            BlackEloDelta = session.BlackEloDelta,
            WhiteBid = whiteBid,
            BlackBid = blackBid,
            MapSeed = session.MapSeed,
            SettingsJson = session.SettingsJson,
            BoardSize = session.BoardSize,
            TimeControlEnabled = session.TimeControlEnabled,
            StartTimeMinutes = session.StartTimeMinutes,
            TimeIncrementSeconds = session.TimeIncrementSeconds,
            MovesJson = JsonSerializer.Serialize(game.MoveList.Select(m => m.ToNotation())),
            MoveCount = game.MoveList.Count,
            Result = game.Result,
            CreatedAt = session.StartedAt,
            EndedAt = DateTime.UtcNow
        };

        // Save historic game and delete session
        db.HistoricGames.Add(historicGame);
        db.GameSessions.Remove(session);
        await db.SaveChangesAsync();
    }

    private static async Task ApplyEloUpdates(AppDbContext db, ServerGame game, GameSession session)
    {
        // Unranked games (incl. all games vs the computer) are stored in history but don't change ratings
        if (!game.Settings.IsRanked || game.IsComputerGame) return;
        if (!game.WhiteUserId.HasValue || !game.BlackUserId.HasValue) return;

        double? whiteScore = game.Result switch
        {
            "w+c" or "w+g" or "w+s" or "w+t" or "w+r" => 1.0,
            "b+c" or "b+g" or "b+s" or "b+t" or "b+r" => 0.0,
            "draw" => 0.5,
            _ => null   // "abandoned": no rating change
        };
        if (!whiteScore.HasValue) return;

        var whiteUser = await db.Users.FindAsync(game.WhiteUserId.Value);
        var blackUser = await db.Users.FindAsync(game.BlackUserId.Value);
        if (whiteUser == null || blackUser == null) return;

        var category = EloService.GetCategory(new GameSettings
        {
            TimeControlEnabled = session.TimeControlEnabled,
            StartTimeMinutes   = session.StartTimeMinutes,
        });

        var (wElo, bElo) = category switch
        {
            TimeControlCategory.Bullet => (whiteUser.EloBullet, blackUser.EloBullet),
            TimeControlCategory.Blitz  => (whiteUser.EloBlitz,  blackUser.EloBlitz),
            TimeControlCategory.Rapid  => (whiteUser.EloRapid,  blackUser.EloRapid),
            _                          => (whiteUser.EloSlow,   blackUser.EloSlow),
        };

        var (wGames, bGames) = category switch
        {
            TimeControlCategory.Bullet => (whiteUser.BulletGamesPlayed, blackUser.BulletGamesPlayed),
            TimeControlCategory.Blitz  => (whiteUser.BlitzGamesPlayed,  blackUser.BlitzGamesPlayed),
            TimeControlCategory.Rapid  => (whiteUser.RapidGamesPlayed,  blackUser.RapidGamesPlayed),
            _                          => (whiteUser.SlowGamesPlayed,   blackUser.SlowGamesPlayed),
        };

        var (newWhite, newBlack) = EloService.Calculate(wElo, bElo, wGames, bGames, whiteScore.Value);

        // Snapshot pre-game ratings and deltas on the session record
        session.WhiteEloAtGame = wElo;
        session.BlackEloAtGame = bElo;
        session.WhiteEloDelta  = newWhite - wElo;
        session.BlackEloDelta  = newBlack - bElo;

        switch (category)
        {
            case TimeControlCategory.Bullet:
                whiteUser.EloBullet = newWhite; whiteUser.BulletGamesPlayed++;
                blackUser.EloBullet = newBlack; blackUser.BulletGamesPlayed++;
                break;
            case TimeControlCategory.Blitz:
                whiteUser.EloBlitz = newWhite; whiteUser.BlitzGamesPlayed++;
                blackUser.EloBlitz = newBlack; blackUser.BlitzGamesPlayed++;
                break;
            case TimeControlCategory.Rapid:
                whiteUser.EloRapid = newWhite; whiteUser.RapidGamesPlayed++;
                blackUser.EloRapid = newBlack; blackUser.RapidGamesPlayed++;
                break;
            default:
                whiteUser.EloSlow = newWhite; whiteUser.SlowGamesPlayed++;
                blackUser.EloSlow = newBlack; blackUser.SlowGamesPlayed++;
                break;
        }

        whiteUser.GamesPlayed++;
        blackUser.GamesPlayed++;

        // Persist the actual (post-swap) user IDs into the session record
        session.WhiteUserId = game.WhiteUserId;
        session.BlackUserId = game.BlackUserId;
    }
}
