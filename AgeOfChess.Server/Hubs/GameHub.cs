using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AgeOfChess.Server.Hubs;

/// <summary>
/// Real-time hub for in-game communication.
/// Clients join a group per gameSessionId when a game starts.
/// </summary>
[Authorize]
public class GameHub : Hub
{
    // Methods will be added here as game features are implemented:
    //   - JoinGame(int gameSessionId)
    //   - MakeMove(int gameSessionId, string move)
    //   - SubmitBid(int gameSessionId, int bid)
    //   - Resign(int gameSessionId)
    //
    // Server -> Client events pushed via Clients.Group(gameSessionId):
    //   - OpponentMoved(string move)
    //   - GameStarted(object gameState)
    //   - GameEnded(string result)
    //   - BidReceived()
}
