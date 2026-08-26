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
}
