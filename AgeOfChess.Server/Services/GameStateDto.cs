using AgeOfChess.Server.GameLogic;
using AgeOfChess.Server.GameLogic.Map;
using AgeOfChess.Server.GameLogic.PlaceableObjects;
using AgeOfChess.Server.GameLogic.PlaceableObjects.Pieces;

namespace AgeOfChess.Server.Services;

/// <summary>
/// Full game state pushed to both clients after every state change.
/// Contains everything needed to render the board without any follow-up requests.
/// </summary>
public record GameStateDto(
    int SessionId,
    string MapSeed,
    IReadOnlyList<SquareDto> Squares,
    PlayerDto White,
    PlayerDto Black,
    IReadOnlyList<string> Moves,
    string? Result,
    bool GameEnded,
    /// <summary>
    /// Unix milliseconds when the current turn started. 0 if the clocks aren't running yet (bidding).
    /// </summary>
    long TurnStartedAt,
    /// <summary>White's first move: time from TurnStartedAt before the main clock starts (0 otherwise).</summary>
    int FirstMoveAllowanceMs,
    bool TimeControlEnabled,
    bool IsRanked,
    /// <summary>Computer opponent: level, side and node budget per move (null/false for human games);
    /// ComputerNnue: search with the evaluation network.</summary>
    int? ComputerLevel,
    bool? ComputerIsWhite,
    int? ComputerNodes,
    bool ComputerNnue,
    /// <summary>Weak computer levels: chance to play the second-best move, and the max loss (gold) allowed.</summary>
    double ComputerMistakeRate,
    double ComputerMistakeMaxLoss,
    bool BiddingEnabled,
    bool BiddingInProgress,
    /// <summary>Standing draw offer: "w", "b" or null.</summary>
    string? DrawOfferBy,
    /// <summary>Handicap option ("none" | "auto" | "manual") and manual amount, for rematches.</summary>
    string Handicap,
    int? HandicapGold,
    /// <summary>Players connected to the game (the computer always is).</summary>
    bool WhiteConnected,
    bool BlackConnected,
    /// <summary>Waiting for a player to connect to the just-started game: Unix ms when it's aborted (0 otherwise).</summary>
    long AbortAt
);

public record SquareDto(
    int X,
    int Y,
    int Id,
    string Type,
    string Highlight,
    PieceDto? Piece,
    string? MineOwner
);

public record PieceDto(string Type, bool IsWhite);

public record PlayerDto(
    string Name,
    int Gold,
    int TimeMsRemaining,
    bool IsActive,
    int? Elo
);

public static class GameStateDtoBuilder
{
    public static GameStateDto Build(ServerGame game) =>
        new(
            game.SessionId,
            game.GetMap().Seed,
            game.GetMap().Squares.Select(s => new SquareDto(
                s.X, s.Y, s.Id,
                s.Type.ToString(),
                s.TemporaryColor.ToString(),
                s.Object is Piece p
                    ? new PieceDto(s.Object.GetType().Name, p.IsWhite)
                    : s.Object != null
                        ? new PieceDto(s.Object.GetType().Name, false)
                        : null,
                s.MineOwner
            )).ToList(),
            new PlayerDto(game.White.PlayedByStr, game.White.Gold, game.White.TimeMiliseconds, game.White.IsActive, game.WhiteElo),
            new PlayerDto(game.Black.PlayedByStr, game.Black.Gold, game.Black.TimeMiliseconds, game.Black.IsActive, game.BlackElo),
            game.MoveList.Select(m => m.ToNotation()).ToList(),
            game.Result,
            game.GameEnded,
            game.LastMoveTimestamp.HasValue
                ? new DateTimeOffset(game.LastMoveTimestamp.Value, TimeSpan.Zero).ToUnixTimeMilliseconds()
                : 0L,
            game.CurrentAllowanceMs,
            game.TimeControlEnabled,
            game.Settings.IsRanked,
            game.Settings.ComputerLevel,
            game.Settings.ComputerIsWhite,
            ComputerLevels.Get(game.Settings.ComputerLevel)?.Nodes,
            ComputerLevels.Get(game.Settings.ComputerLevel)?.Nnue ?? false,
            ComputerLevels.Get(game.Settings.ComputerLevel)?.MistakeRate ?? 0,
            ComputerLevels.Get(game.Settings.ComputerLevel)?.MistakeMaxLoss ?? 0,
            game.BiddingEnabled,
            game.Bidding != null && !game.Bidding.BothBid,
            game.DrawOfferByWhite switch { true => "w", false => "b", null => null },
            game.Settings.Handicap,
            game.Settings.HandicapGold,
            game.SideReady(true),
            game.SideReady(false),
            game.WaitingSince is { } since
                ? new DateTimeOffset(since + ServerGame.WaitForPlayersLimit, TimeSpan.Zero).ToUnixTimeMilliseconds()
                : 0L
        );
}
