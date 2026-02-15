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
    bool GameEnded
);

public record SquareDto(
    int X,
    int Y,
    int Id,
    string Type,
    string Highlight,
    PieceDto? Piece
);

public record PieceDto(string Type, bool IsWhite);

public record PlayerDto(
    string Name,
    int Gold,
    int TimeMsRemaining,
    bool IsActive
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
                        : null
            )).ToList(),
            new PlayerDto(game.White.PlayedByStr, game.White.Gold, game.White.TimeMiliseconds, game.White.IsActive),
            new PlayerDto(game.Black.PlayedByStr, game.Black.Gold, game.Black.TimeMiliseconds, game.Black.IsActive),
            game.MoveList.Select(m => m.ToNotation()).ToList(),
            game.Result,
            game.GameEnded
        );
}
