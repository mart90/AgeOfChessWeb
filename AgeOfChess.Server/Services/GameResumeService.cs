using System.Text.Json;
using AgeOfChess.Server.Data;
using AgeOfChess.Server.Data.Models;
using AgeOfChess.Server.GameLogic;
using AgeOfChess.Server.GameLogic.PlaceableObjects.Pieces;
using Microsoft.EntityFrameworkCore;

namespace AgeOfChess.Server.Services;

/// <summary>
/// Run once at startup. Finds all in-progress slow games in the database and
/// reconstructs their ServerGame instances by replaying the stored move list.
/// Only slow games are restored; faster time controls are abandoned on restart.
/// </summary>
public class GameResumeService(AppDbContext db, GameSessionManager sessions)
{
    public void ResumeSlowGames()
    {
        var inProgressSessions = db.GameSessions
            .Where(s => s.Result == GameResult.InProgress)
            .Include(s => s.WhiteUser)
            .Include(s => s.BlackUser)
            .ToList();

        foreach (var session in inProgressSessions)
        {
            try
            {
                TryResumeGame(session);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[GameResumeService] Failed to resume game {session.Id}: {ex.Message}");
            }
        }
    }

    private void TryResumeGame(GameSession session)
    {
        // Only restore slow games — use denormalized columns to avoid JSON parse
        bool isSlow = !session.TimeControlEnabled || session.StartTimeMinutes >= 30;
        if (!isSlow) return;

        var settings = JsonSerializer.Deserialize<GameSettings>(session.SettingsJson);
        if (settings == null) return;

        // Need the map seed to recreate the exact same board
        if (string.IsNullOrEmpty(session.MapSeed)) return;

        var moveNotations = JsonSerializer.Deserialize<List<string>>(session.MovesJson) ?? [];

        // Don't restore games that never got past the lobby/bidding phase with no moves made
        if (moveNotations.Count == 0 && !session.BlackStartingGold.HasValue) return;

        settings.MapSeed = session.MapSeed;

        // Disable time control during replay so EndTurn doesn't deduct elapsed time
        bool hadTimeControl = settings.TimeControlEnabled;
        settings.TimeControlEnabled = false;

        string whiteName = session.WhiteUser?.EffectiveDisplayName ?? "Player 1";
        string blackName = session.BlackUser?.EffectiveDisplayName ?? "Player 2";

        var game = new ServerGame(
            session.Id, settings,
            session.WhitePlayerToken, session.BlackPlayerToken,
            whiteName, blackName);

        game.WhiteUserId = session.WhiteUserId;
        game.BlackUserId = session.BlackUserId;

        // Set post-bidding starting gold for Black (null = default 10g which the constructor already set)
        if (session.BlackStartingGold.HasValue)
            game.Black.Gold = session.BlackStartingGold.Value;

        // Replay all stored moves to reconstruct the board position
        foreach (var notation in moveNotations)
        {
            var clean = notation.TrimEnd('+', '#');
            bool applied;

            if (clean.Contains('='))
            {
                // Piece placement: "f2=Q" or "f2=p"
                var eqIdx = clean.IndexOf('=');
                var (toX, toY) = ParseSquare(clean[..eqIdx]);
                var pieceType = PieceTypeFromCode(clean[(eqIdx + 1)..].ToLower());
                if (pieceType == null) throw new Exception($"Unknown piece code in notation: {notation}");
                applied = game.TryPlacePiece(toX, toY, pieceType, skipGoldCheck: true);
            }
            else
            {
                // Move: "e1-f2" or "e1xf2"
                char sep = clean.Contains('x') ? 'x' : '-';
                int sepIdx = clean.IndexOf(sep);
                var (fromX, fromY) = ParseSquare(clean[..sepIdx]);
                var (toX, toY)     = ParseSquare(clean[(sepIdx + 1)..]);
                applied = game.TryMovePiece(fromX, fromY, toX, toY);
            }

            if (!applied) throw new Exception($"Failed to replay move: {notation}");

            game.EndTurn();
            game.StartNewTurn();

            if (game.GameEnded)
                throw new Exception($"Game ended unexpectedly during replay at: {notation}");
        }

        // Restore time control and the last saved clock state
        if (hadTimeControl)
        {
            game.TimeControlEnabled = true;
            game.White.TimeMiliseconds = session.WhiteTimeMsRemaining ?? (settings.StartTimeMinutes * 60 * 1000);
            game.Black.TimeMiliseconds = session.BlackTimeMsRemaining ?? (settings.StartTimeMinutes * 60 * 1000);
            // LastMoveTimestamp was already set to DateTime.UtcNow by the final EndTurn replay call,
            // so the clock starts from the restored time when the next real move comes in.
        }

        game.HasGameStarted = true;

        sessions.Add(game);
        Console.WriteLine($"[GameResumeService] Resumed game {session.Id} ({moveNotations.Count} moves, {whiteName} vs {blackName})");
    }

    private static (int x, int y) ParseSquare(string sq)
    {
        int x = sq[0] - 'a';
        int y = int.Parse(sq[1..]) - 1;
        return (x, y);
    }

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
