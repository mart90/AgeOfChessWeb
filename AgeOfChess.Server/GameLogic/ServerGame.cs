using AgeOfChess.Server.GameLogic.Map;

namespace AgeOfChess.Server.GameLogic;

/// <summary>
/// Concrete Game subclass used by the server. Initialises the map and players
/// from GameSettings and holds the SignalR connection IDs for each player.
/// </summary>
public class ServerGame : Game
{
    public int SessionId { get; }
    public string WhitePlayerToken { get; }
    public string BlackPlayerToken { get; }

    // SignalR connection IDs — set when each client connects to the hub
    public string? WhiteConnectionId { get; set; }
    public string? BlackConnectionId { get; set; }

    public bool WhiteConnected => WhiteConnectionId != null;
    public bool BlackConnected => BlackConnectionId != null;

    public string GroupName => $"game-{SessionId}";

    public ServerGame(int sessionId, GameSettings settings, string whitePlayerToken, string blackPlayerToken, string whitePlayerName, string blackPlayerName)
    {
        SessionId = sessionId;
        WhitePlayerToken = whitePlayerToken;
        BlackPlayerToken = blackPlayerToken;

        TimeControlEnabled = settings.TimeControlEnabled;
        TimeIncrementSeconds = settings.TimeIncrementSeconds;

        Colors = new List<PieceColor>
        {
            new PieceColor(true, whitePlayerName),
            new PieceColor(false, blackPlayerName)
        };

        if (settings.TimeControlEnabled)
        {
            White.TimeMiliseconds = settings.StartTimeMinutes * 60 * 1000;
            Black.TimeMiliseconds = settings.StartTimeMinutes * 60 * 1000;
        }

        var generator = new MapGenerator();
        Map = settings.MapSeed != null
            ? generator.GenerateFromSeed(settings.MapSeed)
            : generator.GenerateRandom(settings.BoardSize, settings.BoardSize);
    }

    /// <summary>
    /// Returns the player token for a given connection ID, or null if not found.
    /// </summary>
    public string? GetPlayerToken(string connectionId)
    {
        if (connectionId == WhiteConnectionId) return WhitePlayerToken;
        if (connectionId == BlackConnectionId) return BlackPlayerToken;
        return null;
    }

    public bool IsWhitePlayer(string playerToken) => playerToken == WhitePlayerToken;
}
