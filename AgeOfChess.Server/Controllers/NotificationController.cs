using System.Security.Claims;
using AgeOfChess.Server.Data;
using AgeOfChess.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgeOfChess.Server.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationController(AppDbContext db, NotificationService notifications) : ControllerBase
{
    private const int ListLimit = 50;

    public record MarkReadRequest(int[]? Ids = null, int? GameId = null);

    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/notifications  — unread notifications, newest first
    [HttpGet]
    public async Task<IActionResult> GetUnread()
    {
        var list = await db.Notifications
            .Where(n => n.UserId == UserId && n.ReadAt == null)
            .OrderByDescending(n => n.CreatedAt)
            .Take(ListLimit)
            .ToListAsync();
        return Ok(list.Select(NotificationService.ToDto));
    }

    // POST /api/notifications/read  — mark notifications read: by id, or all about a game
    [HttpPost("read")]
    public async Task<IActionResult> MarkRead([FromBody] MarkReadRequest req)
    {
        var query = db.Notifications.Where(n => n.UserId == UserId && n.ReadAt == null);
        if (req.Ids is { Length: > 0 } ids) query = query.Where(n => ids.Contains(n.Id));
        else if (req.GameId is int gameId)  query = query.Where(n => n.GameId == gameId);
        else return BadRequest();

        int changed = await query.ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTime.UtcNow));
        if (changed > 0) await notifications.ChangedAsync(UserId);
        return Ok();
    }
}
