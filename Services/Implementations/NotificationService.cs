using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// Persists notifications to the database and pushes them live to connected Blazor Server
/// components via the in-process <see cref="INotificationBroker"/>. Registered as a singleton,
/// so it resolves a scoped <see cref="AppDbContext"/> per operation via the scope factory.
/// </summary>
public class NotificationService(
    IServiceScopeFactory scopeFactory,
    INotificationBroker broker,
    ILogger<NotificationService> logger) : INotificationService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly INotificationBroker _broker = broker;
    private readonly ILogger<NotificationService> _logger = logger;

    // Each lifecycle event: (1) in-app notification/broker + (2) a Discord bridge outbox row.
    // This is the single choke point for all transitions (web UI, fulfillment, and bridge paths).

    public async Task RequestCreatedAsync(MediaRequestDto request)
    {
        await NotifyAdminsAsync(NotificationType.RequestCreated, "New request",
            $"{request.RequestedByUsername} requested \"{request.Title}\"", request.Id);
        await WriteOutboxAsync(request, BridgeEventType.Created);
    }

    public async Task RequestApprovedAsync(MediaRequestDto request)
    {
        await NotifyUserAsync(request.RequestedByUserId, NotificationType.RequestApproved,
            "Request approved", $"Your request for \"{request.Title}\" was approved.", request.Id);
        await WriteOutboxAsync(request, BridgeEventType.Approved);
    }

    public async Task RequestRejectedAsync(MediaRequestDto request)
    {
        await NotifyUserAsync(request.RequestedByUserId, NotificationType.RequestRejected,
            "Request denied",
            $"Your request for \"{request.Title}\" was denied." +
                (string.IsNullOrWhiteSpace(request.DenialReason) ? "" : $" Reason: {request.DenialReason}"),
            request.Id);
        await WriteOutboxAsync(request, BridgeEventType.Denied, request.DenialReason);
    }

    public async Task RequestAvailableAsync(MediaRequestDto request)
    {
        await NotifyUserAsync(request.RequestedByUserId, NotificationType.RequestAvailable,
            "Now available", $"\"{request.Title}\" is now available to watch.", request.Id);
        await WriteOutboxAsync(request, BridgeEventType.Available);
    }

    public async Task RequestPartiallyAvailableAsync(MediaRequestDto request, string reason)
    {
        await NotifyUserAsync(request.RequestedByUserId, NotificationType.Warning,
            "Partially available",
            $"Some of \"{request.Title}\" is available, but the remaining content is still missing.", request.Id);
        await WriteOutboxAsync(request, BridgeEventType.PartiallyAvailable, reason);
    }

    public async Task RequestFailedAsync(MediaRequestDto request, string reason)
    {
        // The user-facing message stays generic; the raw reason (which may contain exception text from the
        // downloader) is kept in the server logs and the Discord bridge outbox for troubleshooting.
        await NotifyAdminsAsync(NotificationType.Error, "Fulfillment failed",
            $"We hit a problem downloading \"{request.Title}\". Check the server logs for details.", request.Id);
        await WriteOutboxAsync(request, BridgeEventType.Failed, reason);
    }

    public Task RequestCancelledAsync(MediaRequestDto request) =>
        WriteOutboxAsync(request, BridgeEventType.Cancelled);

    public async Task RequestSearchStalledAsync(MediaRequestDto request, int attempts)
    {
        // Not a failure — the request is still being searched. Surface it to admins so they can add an
        // indexer or relax quality rules if they want it found sooner.
        await NotifyAdminsAsync(NotificationType.RequestSearchStalled, "Still searching",
            $"\"{request.Title}\" has been searched {attempts} times with no release found yet. It will keep retrying; you can help by adding an indexer or adjusting quality rules.",
            request.Id);
        await WriteOutboxAsync(request, BridgeEventType.Searching,
            $"Still searching after {attempts} attempts");
    }

    public async Task RequestUpgradedAsync(MediaRequestDto request, Quality newQuality)
    {
        await NotifyUserAsync(request.RequestedByUserId, NotificationType.RequestUpgraded,
            "Quality upgraded", $"\"{request.Title}\" was upgraded to {newQuality.Label()}.", request.Id);
        await WriteOutboxAsync(request, BridgeEventType.Upgraded, $"Upgraded to {newQuality.Label()}");
    }

    public async Task RequestReplacedAsync(MediaRequestDto request, string target, int? issueReporterUserId = null)
    {
        foreach (var userId in new[] { request.RequestedByUserId, issueReporterUserId ?? 0 }.Where(x => x > 0).Distinct())
            await NotifyUserAsync(userId, NotificationType.RequestReplaced,
                "Reported file replaced", $"A new copy of \"{request.Title}\" {target} is ready.", request.Id);
        await WriteOutboxAsync(request, BridgeEventType.Upgraded, $"Issue replacement completed for {target}");
    }

    public Task MediaIssueReportedAsync(MediaIssueDto issue) =>
        NotifyAdminsAsync(NotificationType.MediaIssueReported, "Media problem reported",
            $"{issue.ReportedBy ?? "A user"} reported {IssueTarget(issue)} on \"{issue.Title}\": {issue.Reason}", null);

    public Task MediaIssueApprovedAsync(MediaIssueDto issue) => issue.ReportedByUserId is int userId
        ? NotifyUserAsync(userId, NotificationType.MediaIssueApproved, "Replacement approved",
            $"A replacement for \"{issue.Title}\" {IssueTarget(issue)} was approved and queued.", null)
        : Task.CompletedTask;

    public Task MediaIssueClosedAsync(MediaIssueDto issue, bool dismissed) => issue.ReportedByUserId is int userId
        ? NotifyUserAsync(userId,
            dismissed ? NotificationType.MediaIssueDismissed : NotificationType.MediaIssueResolved,
            dismissed ? "Report closed" : "Report resolved",
            dismissed
                ? $"Your report for \"{issue.Title}\" {IssueTarget(issue)} was reviewed and closed."
                : $"Your report for \"{issue.Title}\" {IssueTarget(issue)} was marked resolved.", null)
        : Task.CompletedTask;

    private static string IssueTarget(MediaIssueDto issue) => issue.SeasonNumber is int season && issue.EpisodeNumber is int episode
        ? $"episode S{season:D2}E{episode:D2}"
        : "the title";

    private async Task WriteOutboxAsync(MediaRequestDto request, BridgeEventType type, string? detail = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IBridgeOutboxService>();
            await outbox.EnqueueAsync(request, type, detail);
        }
        catch (Exception ex)
        {
            // The lifecycle transition itself must not fail because an optional bridge is unavailable.
            // Unlike the former empty catch, this is observable, and the outbox repair pass can recreate
            // the current state on the next successful bot poll.
            _logger.LogWarning(ex, "Could not persist bridge event {EventType} for request {RequestId}",
                type, request.Id);
        }
    }

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
            var access = scope.ServiceProvider.GetRequiredService<IUserAccessService>();
            var userIds = await db.UserProfiles.Select(p => p.UserId).ToListAsync();
            adminUserIds = [];
            foreach (var userId in userIds)
                if (await access.IsAdminAsync(userId)) adminUserIds.Add(userId);
        }

        foreach (var uid in adminUserIds)
            await PersistAndPublishAsync(uid, type, title, message, relatedRequestId);
    }

    private async Task PersistAndPublishAsync(int userId, NotificationType type, string title, string message, int? relatedRequestId)
    {
        NotificationDto? dto = null;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var enabledTypes = await db.UserProfiles
                .Where(x => x.UserId == userId)
                .Select(x => x.WebNotificationTypes)
                .FirstOrDefaultAsync();
            if ((enabledTypes & NotificationPreferencesDto.Bit(type)) == 0) return;

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
        if (dto is not null) _broker.Publish(dto);
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
