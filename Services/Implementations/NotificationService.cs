using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// Persists notifications to the database and pushes them live to connected Blazor Server
/// components via the in-process <see cref="INotificationBroker"/>. Registered as a singleton,
/// so it resolves a scoped <see cref="AppDbContext"/> per operation via the scope factory.
/// </summary>
public class NotificationService(IServiceScopeFactory scopeFactory, INotificationBroker broker) : INotificationService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly INotificationBroker _broker = broker;

    public Task RequestCreatedAsync(MediaRequestDto request) =>
        NotifyAdminsAsync(NotificationType.RequestCreated, "New request",
            $"{request.RequestedByUsername} requested \"{request.Title}\"", request.Id);

    public Task RequestApprovedAsync(MediaRequestDto request) =>
        NotifyUserAsync(request.RequestedByUserId, NotificationType.RequestApproved,
            "Request approved", $"Your request for \"{request.Title}\" was approved.", request.Id);

    public Task RequestRejectedAsync(MediaRequestDto request) =>
        NotifyUserAsync(request.RequestedByUserId, NotificationType.RequestRejected,
            "Request denied",
            $"Your request for \"{request.Title}\" was denied." +
                (string.IsNullOrWhiteSpace(request.DenialReason) ? "" : $" Reason: {request.DenialReason}"),
            request.Id);

    public Task RequestAvailableAsync(MediaRequestDto request) =>
        NotifyUserAsync(request.RequestedByUserId, NotificationType.RequestAvailable,
            "Now available", $"\"{request.Title}\" is now available to watch.", request.Id);

    public Task RequestFailedAsync(MediaRequestDto request, string reason) =>
        NotifyAdminsAsync(NotificationType.Error, "Fulfillment failed",
            $"Download failed for \"{request.Title}\": {reason}", request.Id);

    private async Task NotifyUserAsync(int userId, NotificationType type, string title, string message, int? relatedRequestId)
    {
        if (userId <= 0) return; // legacy requests without a linked user id
        await PersistAndPublishAsync(userId, type, title, message, relatedRequestId);
    }

    private async Task NotifyAdminsAsync(NotificationType type, string title, string message, int? relatedRequestId)
    {
        List<int> adminUserIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profiles = await db.UserProfiles.Select(p => new { p.UserId, p.Roles }).ToListAsync();
            adminUserIds = profiles
                .Where(p => (p.Roles ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains("Admin", StringComparer.OrdinalIgnoreCase))
                .Select(p => p.UserId)
                .Distinct()
                .ToList();
        }

        foreach (var uid in adminUserIds)
            await PersistAndPublishAsync(uid, type, title, message, relatedRequestId);
    }

    private async Task PersistAndPublishAsync(int userId, NotificationType type, string title, string message, int? relatedRequestId)
    {
        NotificationDto dto;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var entity = new NotificationEntity
            {
                UserId = userId,
                Title = title,
                Message = message,
                Type = type,
                RelatedRequestId = relatedRequestId,
                IsRead = false,
                CreatedAt = DateTime.UtcNow
            };
            db.Notifications.Add(entity);
            await db.SaveChangesAsync();
            dto = Map(entity);
        }
        _broker.Publish(dto);
    }

    public async Task<List<NotificationDto>> GetForUserAsync(int userId, int take = 20)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Notifications
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(take)
            .Select(n => new NotificationDto
            {
                Id = n.Id,
                UserId = n.UserId,
                Title = n.Title,
                Message = n.Message,
                Type = n.Type,
                IsRead = n.IsRead,
                CreatedAt = n.CreatedAt,
                RelatedRequestId = n.RelatedRequestId
            })
            .ToListAsync();
    }

    public async Task<int> GetUnreadCountAsync(int userId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);
    }

    public async Task MarkAllReadAsync(int userId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Notifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
    }

    public async Task MarkReadAsync(int notificationId, int userId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Notifications
            .Where(n => n.Id == notificationId && n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
    }

    private static NotificationDto Map(NotificationEntity n) => new()
    {
        Id = n.Id,
        UserId = n.UserId,
        Title = n.Title,
        Message = n.Message,
        Type = n.Type,
        IsRead = n.IsRead,
        CreatedAt = n.CreatedAt,
        RelatedRequestId = n.RelatedRequestId
    };
}
