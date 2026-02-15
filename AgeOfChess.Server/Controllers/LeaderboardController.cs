using Microsoft.AspNetCore.Mvc;

namespace AgeOfChess.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LeaderboardController : ControllerBase
{
    // GET /api/leaderboard -> top players by Elo
}
