using System.Collections.Concurrent;
using AgeOfChess.Server.GameLogic;

namespace AgeOfChess.Server.Services;

/// <summary>
/// Singleton that holds all in-progress games in memory.
/// The database record is the persistent store; this is the live state.
/// </summary>
public class GameSessionManager
{
    private readonly ConcurrentDictionary<int, ServerGame> _games = new();

    // Token → sessionId lookup so the hub can find a game from a player token
    private readonly ConcurrentDictionary<string, int> _tokenIndex = new();

    public void Add(ServerGame game)
    {
        _games[game.SessionId] = game;
        _tokenIndex[game.WhitePlayerToken] = game.SessionId;
        _tokenIndex[game.BlackPlayerToken] = game.SessionId;
    }

    public ServerGame? GetById(int sessionId) =>
        _games.GetValueOrDefault(sessionId);

    public ServerGame? GetByPlayerToken(string token)
    {
        if (_tokenIndex.TryGetValue(token, out int id))
            return GetById(id);
        return null;
    }

    public void Remove(int sessionId)
    {
        if (_games.TryRemove(sessionId, out var game))
        {
            _tokenIndex.TryRemove(game.WhitePlayerToken, out _);
            _tokenIndex.TryRemove(game.BlackPlayerToken, out _);
        }
    }
}
