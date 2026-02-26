using AgeOfChess.Server.GameLogic;
using AgeOfChess.Server.GameLogic.Map;
using AgeOfChess.Server.GameLogic.PlaceableObjects.GaiaObjects;
using AgeOfChess.Server.GameLogic.PlaceableObjects.Pieces;
using AgeOfChess.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgeOfChess.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SandboxController(IConfiguration config) : ControllerBase
{
    public record GenerateBoardRequest(
        int     Size      = 12,
        bool    IsRandom  = true,
        string? Seed      = null
    );
    
    public record GenerateBoardsBulkRequest(
        int     Size      = 10,
        bool    IsRandom  = true,
        string? Seed      = null,
        int     Amount    = 1,
        string? Token     = null
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

    // POST /api/sandbox/generateBulk  — generate multiple boards in 1 request
    [HttpPost("generateBulk")]
    public IActionResult GenerateBoardsBulk([FromBody] GenerateBoardsBulkRequest req)
    {
        if (req.Token != config["GenerateBulkToken"])
        {
            return Forbid();
        }

        var generator = new MapGenerator();
        var maps = new List<Map>();

        try
        {
            for (int i = 0; i < req.Amount; i++)
            {
                var map = req.IsRandom ? generator.GenerateFullRandom(req.Size, req.Size) : generator.GenerateMirrored(req.Size, req.Size);
                maps.Add(map);
            }
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }

        return Ok(maps.Select(e => new
        {
            squares = e.Squares.Select(s => new
            {
                s.X,
                s.Y,
                Type = s.Type.ToString(),
                HasTreasure = s.Object != null && s.Object is Treasure,
                PieceType = s.Object != null && s.Object is King ? "k" : null,
                IsWhite = s.Object != null && s.Object is King ? s.Object is WhiteKing : (bool?)null
            }),
        }));
    }
}
