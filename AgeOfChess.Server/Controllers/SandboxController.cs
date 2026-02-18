using AgeOfChess.Server.GameLogic;
using AgeOfChess.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgeOfChess.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SandboxController : ControllerBase
{
    public record GenerateBoardRequest(
        int     Size      = 12,
        bool    IsRandom  = true,
        string? Seed      = null
    );

    // POST /api/sandbox/board  — generate (or parse) a map; returns board state only (no game session)
    [HttpPost("board")]
    public IActionResult GenerateBoard([FromBody] GenerateBoardRequest req)
    {
        int size = Math.Clamp(req.Size, 6, 16);
        if (size % 2 != 0) size = size > 6 ? size - 1 : 6;

        string? seed = req.Seed?.Trim();
        if (string.IsNullOrEmpty(seed)) seed = null;

        var settings = new GameSettings
        {
            BoardSize          = size,
            MapMode            = req.IsRandom ? "r" : "m",
            MapSeed            = seed,
            TimeControlEnabled = false,
        };

        try
        {
            var game = new ServerGame(0, settings, "", "", "White", "Black");
            var dto  = GameStateDtoBuilder.Build(game);

            return Ok(new
            {
                mapSeed = dto.MapSeed,
                mapSize = game.MapSize,
                squares = dto.Squares,
            });
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
