using AgeOfChess.Server.Data;
using AgeOfChess.Server.Data.Models;
using AgeOfChess.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AgeOfChess.Server.Services;

/// <summary>
/// Notifications: stored (so they reach users who are offline or whose tab is stale) and pushed
/// live to every open tab of the user (their game hub connections are in <see cref="UserGroup"/>).
///
/// Server → client: Notification(NotificationDto) for a new one; NotificationsChanged() when some
/// were read or removed (tabs fetch the list again).
/// </summary>
public class NotificationService(IHubContext<GameHub> hub, IServiceScopeFactory scopeFactory)
{
    public record NotificationDto(int Id, string Type, int? GameId, string? OpponentName, DateTime CreatedAt);

    public static string UserGroup(int userId) => $"user:{userId}";

    public static NotificationDto ToDto(Notification n) => new(n.Id, n.Type, n.GameId, n.OpponentName, n.CreatedAt);

    public async Task GameStartedAsync(int userId, int gameId, string opponentName)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var n = new Notification
        {
            UserId = userId, Type = Notification.Types.GameStarted, GameId = gameId, OpponentName = opponentName,
        };
        db.Notifications.Add(n);
        await db.SaveChangesAsync();
        await hub.Clients.Group(UserGroup(userId)).SendAsync("Notification", ToDto(n));
    }

    /// <summary>Removes the notifications about a game (e.g. it was aborted).</summary>
    public async Task RemoveForGameAsync(int gameId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userIds = await db.Notifications.Where(n => n.GameId == gameId).Select(n => n.UserId).Distinct().ToListAsync();
        if (userIds.Count == 0) return;
        await db.Notifications.Where(n => n.GameId == gameId).ExecuteDeleteAsync();
        foreach (var userId in userIds)
            await hub.Clients.Group(UserGroup(userId)).SendAsync("NotificationsChanged");
    }

    /// <summary>Tells the user's other tabs to fetch their notifications again.</summary>
    public Task ChangedAsync(int userId) => hub.Clients.Group(UserGroup(userId)).SendAsync("NotificationsChanged");
}
