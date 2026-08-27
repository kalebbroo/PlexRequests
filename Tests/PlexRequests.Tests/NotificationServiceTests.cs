using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class NotificationServiceTests
{
    [Fact]
    public async Task Completed_replacement_notifies_request_owner_and_distinct_issue_reporter_once_each()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton<IBridgeOutboxService, NoOpOutbox>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Users.AddRange(
                new UserEntity { Id = 7, Username = "request-owner" },
                new UserEntity { Id = 8, Username = "issue-reporter" });
            db.UserProfiles.AddRange(
                new UserProfileEntity
                {
                    UserId = 7,
                    WebNotificationTypes = NotificationPreferencesDto.Bit(NotificationType.RequestReplaced)
                },
                new UserProfileEntity
                {
                    UserId = 8,
                    WebNotificationTypes = NotificationPreferencesDto.Bit(NotificationType.RequestReplaced)
                });
            db.MediaRequests.Add(new MediaRequestEntity
            {
                Id = 50, MediaId = 123, MediaType = MediaType.TvShow, Title = "Shared Show",
                RequestedByUserId = 7
            });
            await db.SaveChangesAsync();
        }

        var notifications = new NotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(), new NotificationBroker(),
            NullLogger<NotificationService>.Instance);
        await notifications.RequestReplacedAsync(new MediaRequestDto
        {
            Id = 50, MediaId = 123, MediaType = MediaType.TvShow, Title = "Shared Show",
            RequestedByUserId = 7
        }, "S01E04", issueReporterUserId: 8);

        await using var verifyScope = provider.CreateAsyncScope();
        var persisted = await verifyScope.ServiceProvider.GetRequiredService<AppDbContext>().Notifications
            .AsNoTracking().OrderBy(n => n.UserId).ToListAsync();
        Assert.Equal([7, 8], persisted.Select(n => n.UserId).ToArray());
        Assert.All(persisted, n => Assert.Equal(NotificationType.RequestReplaced, n.Type));
        Assert.All(persisted, n => Assert.Contains("S01E04", n.Message));
    }

    [Fact]
    public async Task Completed_replacement_deduplicates_when_reporter_is_request_owner()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton<IBridgeOutboxService, NoOpOutbox>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(new UserEntity { Id = 7, Username = "owner-and-reporter" });
            db.UserProfiles.Add(new UserProfileEntity
            {
                UserId = 7,
                WebNotificationTypes = NotificationPreferencesDto.Bit(NotificationType.RequestReplaced)
            });
            db.MediaRequests.Add(new MediaRequestEntity
            {
                Id = 51, MediaId = 456, MediaType = MediaType.Movie, Title = "One Notification",
                RequestedByUserId = 7
            });
            await db.SaveChangesAsync();
        }

        var notifications = new NotificationService(
            provider.GetRequiredService<IServiceScopeFactory>(), new NotificationBroker(),
            NullLogger<NotificationService>.Instance);
        await notifications.RequestReplacedAsync(new MediaRequestDto
        {
            Id = 51, MediaId = 456, MediaType = MediaType.Movie, Title = "One Notification",
            RequestedByUserId = 7
        }, "title", issueReporterUserId: 7);

        await using var verifyScope = provider.CreateAsyncScope();
        Assert.Single(await verifyScope.ServiceProvider.GetRequiredService<AppDbContext>().Notifications.ToListAsync());
    }

    [Fact]
    public async Task Web_notifications_are_opt_in_and_filter_each_event_before_persisting_or_publishing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton<IBridgeOutboxService, NoOpOutbox>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Users.AddRange(
                new UserEntity { Id = 7, Username = "approved-only" },
                new UserEntity { Id = 8, Username = "all-off" });
            db.UserProfiles.AddRange(
                new UserProfileEntity
                {
                    UserId = 7,
                    WebNotificationTypes = NotificationPreferencesDto.Bit(NotificationType.RequestApproved)
                },
                new UserProfileEntity { UserId = 8 });
            db.MediaRequests.AddRange(
                new MediaRequestEntity
                {
                    Id = 50, MediaId = 500, MediaType = MediaType.Movie, Title = "Selected",
                    RequestedByUserId = 7
                },
                new MediaRequestEntity
                {
                    Id = 51, MediaId = 501, MediaType = MediaType.Movie, Title = "Silent",
                    RequestedByUserId = 8
                });
            await db.SaveChangesAsync();
        }

        var broker = new NotificationBroker();
        var published = new List<NotificationDto>();
        broker.Notified += published.Add;
        var notifications = new NotificationService(provider.GetRequiredService<IServiceScopeFactory>(), broker,
            NullLogger<NotificationService>.Instance);

        var optedIn = new MediaRequestDto { Id = 50, Title = "Selected", RequestedByUserId = 7 };
        await notifications.RequestApprovedAsync(optedIn);
        await notifications.RequestAvailableAsync(optedIn);
        await notifications.RequestApprovedAsync(new MediaRequestDto
            { Id = 51, Title = "Silent", RequestedByUserId = 8 });

        await using var verifyScope = provider.CreateAsyncScope();
        var persisted = await verifyScope.ServiceProvider.GetRequiredService<AppDbContext>().Notifications
            .AsNoTracking().ToListAsync();
        var only = Assert.Single(persisted);
        Assert.Equal(NotificationType.RequestApproved, only.Type);
        Assert.Equal(7, only.UserId);
        Assert.Equal(only.Id, Assert.Single(published).Id);
    }

    [Fact]
    public async Task Preference_updates_are_user_scoped_clamped_and_keep_legacy_discord_state_in_sync()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new UserEntity { Id = 7, Username = "chooser" });
        db.UserProfiles.Add(new UserProfileEntity { UserId = 7 });
        await db.SaveChangesAsync();

        var service = new NotificationPreferenceService(db, new FixedAccess(7));
        var selected = new NotificationPreferencesDto();
        selected.SetEnabled(NotificationChannel.Web, NotificationType.RequestAvailable, true);
        selected.SetEnabled(NotificationChannel.Discord, NotificationType.RequestApproved, true);
        selected.DiscordTypeMask |= 1L << 60; // unsupported bits are never persisted

        Assert.True(await service.UpdateCurrentAsync(selected));
        var saved = await service.GetCurrentAsync();
        Assert.True(saved.IsEnabled(NotificationChannel.Web, NotificationType.RequestAvailable));
        Assert.True(saved.IsEnabled(NotificationChannel.Discord, NotificationType.RequestApproved));
        Assert.Equal(0, saved.DiscordTypeMask & (1L << 60));
        Assert.True(await db.UserProfiles.Select(x => x.DiscordDmOptIn).SingleAsync());

        saved.DiscordTypeMask = 0;
        Assert.True(await service.UpdateCurrentAsync(saved));
        Assert.False(await db.UserProfiles.Select(x => x.DiscordDmOptIn).SingleAsync());
    }

    private sealed class NoOpOutbox : IBridgeOutboxService
    {
        public int RetentionDays => 30;
        public Task EnqueueAsync(MediaRequestDto request, BridgeEventType type, string? detail = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BridgeEventBatchDto> ReadBatchAsync(long cursor, int take,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BridgeEventBatchDto());
        public Task<BridgeEventAckResult> AcknowledgeAsync(long cursor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BridgeEventAckResult(cursor, 0, 0));
    }

    private sealed class FixedAccess(int userId) : IUserAccessService
    {
        public Task<int?> GetCurrentUserIdAsync() => Task.FromResult<int?>(userId);
        public Task<UserAccessSnapshot> GetAccessAsync(int id) => Task.FromResult(new UserAccessSnapshot(
            id, UserPermission.AllRequests, null, null, null, 7, null, 7, null, 7, null));
        public Task<bool> CanRequestAsync(int id, MediaType mediaType) => Task.FromResult(true);
        public Task<bool> IsAdminAsync(int id) => Task.FromResult(false);
    }
}
