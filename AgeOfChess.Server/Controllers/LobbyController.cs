using Microsoft.AspNetCore.Mvc;

namespace AgeOfChess.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LobbyController : ControllerBase
{
    // GET  /api/lobby           -> list open lobbies
    // POST /api/lobby           -> create lobby
    // POST /api/lobby/{id}/join -> join lobby (starts game, triggers SignalR GameStarted)
    // DELETE /api/lobby/{id}    -> cancel lobby (host only)
}
