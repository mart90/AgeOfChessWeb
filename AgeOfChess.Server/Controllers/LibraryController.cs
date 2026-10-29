using System.Security.Claims;
using System.Text.Json;
using AgeOfChess.Server.Data;
using AgeOfChess.Server.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgeOfChess.Server.Controllers;

/// <summary>
/// The logged-in user's game library ("My library"): saved games with their variations.
/// Published entries can be opened by anyone through their share link (/analyze/{shareId});
/// only the owner can change them, others save a copy to their own library.
/// </summary>
[ApiController]
[Route("api/library")]
[Authorize]
public class LibraryController(AppDbContext db) : ControllerBase
{
    private const int MaxEntriesPerUser = 1000;
    private const int MaxDataBytes = 8_000_000;
    // The packed tree nests one level per move (+ one for the child list), so games are deep JSON
    private static readonly JsonDocumentOptions DataJsonOptions = new() { MaxDepth = 100_000 };

    public record SaveRequest(
        string Title,
        int? GameId,
        int MapSize,
        string? MapSeed,
        int PlyCount,
        string? Result,
        /// <summary>The packed tree as a JSON string (too deeply nested to bind as an object).</summary>
        string Data);

    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private int? UserIdOrNull => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public record PublishRequest(bool IsPublic);

    // GET /api/library — the user's entries, newest first (without the move trees)
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var entries = await db.LibraryGames
            .Where(g => g.UserId == UserId)
            .OrderByDescending(g => g.UpdatedAt)
            .Select(g => new { g.Id, g.ShareId, g.IsPublic, g.Title, g.GameId, g.MapSize, g.MapSeed, g.PlyCount, g.Result, g.CreatedAt, g.UpdatedAt })
            .ToListAsync();
        return Ok(entries);
    }

    // GET /api/library/{id} — one entry with its move tree
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var g = await db.LibraryGames.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (g == null) return NotFound();
        return EntryJson(g, owned: true, owner: null);
    }

    // GET /api/library/shared/{shareId} — an entry opened through its share link: published ones for
    // anyone, unpublished ones only for the owner. Non-owners don't get the numeric id.
    [HttpGet("shared/{shareId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetShared(Guid shareId)
    {
        var g = await db.LibraryGames.Include(x => x.User).FirstOrDefaultAsync(x => x.ShareId == shareId);
        bool owned = g != null && g.UserId == UserIdOrNull;
        if (g == null || !(g.IsPublic || owned)) return NotFound("This game doesn't exist or isn't published.");
        return EntryJson(g, owned, g.User);
    }

    private ContentResult EntryJson(LibraryGame g, bool owned, User? owner) => Content(
        $"{{\"id\":{(owned ? g.Id.ToString() : "null")},\"shareId\":\"{g.ShareId}\",\"isPublic\":{(g.IsPublic ? "true" : "false")}," +
        $"\"owned\":{(owned ? "true" : "false")},\"owner\":{JsonSerializer.Serialize(owner?.EffectiveDisplayName)},\"ownerUsername\":{JsonSerializer.Serialize(owner?.Username)}," +
        $"\"title\":{JsonSerializer.Serialize(g.Title)},\"result\":{JsonSerializer.Serialize(g.Result)}," +
        $"\"mapSize\":{g.MapSize},\"mapSeed\":{JsonSerializer.Serialize(g.MapSeed)},\"data\":{g.DataJson}}}",
        "application/json");

    // POST /api/library — add an entry
    [HttpPost]
    [RequestSizeLimit(MaxDataBytes + 100_000)]
    public async Task<IActionResult> Add([FromBody] SaveRequest req)
    {
        var error = Validate(req, out var data);
        if (error != null) return BadRequest(error);
        if (await db.LibraryGames.CountAsync(g => g.UserId == UserId) >= MaxEntriesPerUser)
            return BadRequest($"Your library is full ({MaxEntriesPerUser} games).");

        var entry = new LibraryGame { UserId = UserId };
        Apply(entry, req, data);
        db.LibraryGames.Add(entry);
        await db.SaveChangesAsync();
        return Ok(new { entry.Id, entry.ShareId });
    }

    // PUT /api/library/{id} — replace an entry's tree (e.g. after adding variations)
    [HttpPut("{id:int}")]
    [RequestSizeLimit(MaxDataBytes + 100_000)]
    public async Task<IActionResult> Update(int id, [FromBody] SaveRequest req)
    {
        var entry = await db.LibraryGames.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (entry == null) return NotFound();
        var error = Validate(req, out var data);
        if (error != null) return BadRequest(error);

        Apply(entry, req, data);
        entry.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { entry.Id, entry.ShareId });
    }

    // PUT /api/library/{id}/public — publish / unpublish the share link
    [HttpPut("{id:int}/public")]
    public async Task<IActionResult> SetPublic(int id, [FromBody] PublishRequest req)
    {
        var entry = await db.LibraryGames.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (entry == null) return NotFound();
        entry.IsPublic = req.IsPublic;
        await db.SaveChangesAsync();
        return Ok(new { entry.IsPublic });
    }

    // DELETE /api/library/{id}
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var entry = await db.LibraryGames.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (entry == null) return NotFound();
        db.LibraryGames.Remove(entry);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private static string? Validate(SaveRequest req, out string data)
    {
        data = req.Data ?? "";
        if (data.Length > MaxDataBytes) return "This game is too large to save.";
        try
        {
            using var doc = JsonDocument.Parse(data, DataJsonOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("terrain", out var terrain) || terrain.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Object)
                return "Invalid game data.";
        }
        catch (JsonException) { return "Invalid game data."; }
        if (req.MapSize is < 4 or > 32) return "Invalid board size.";
        return null;
    }

    private static void Apply(LibraryGame entry, SaveRequest req, string data)
    {
        string Clip(string? s, int max) => (s ?? "").Trim() is var t && t.Length > max ? t[..max] : (s ?? "").Trim();
        entry.Title    = Clip(req.Title, 200) is { Length: > 0 } title ? title : "Untitled";
        entry.GameId   = req.GameId;
        entry.MapSize  = req.MapSize;
        entry.MapSeed  = string.IsNullOrWhiteSpace(req.MapSeed) ? null : Clip(req.MapSeed, 200);
        entry.PlyCount = Math.Max(0, req.PlyCount);
        entry.Result   = string.IsNullOrWhiteSpace(req.Result) ? null : Clip(req.Result, 10);
        entry.DataJson = data;
    }
}
