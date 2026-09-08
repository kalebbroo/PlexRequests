using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class DownloadMonitorServiceTests
{
    [Fact]
    public async Task JobsStayInStartOrderWhileHeartbeatsChange()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.AddJobAsync("First", FulfillmentStatus.Downloading,
            createdMinutesAgo: 30, claimedMinutesAgo: 20, updatedMinutesAgo: 1);
        var second = await fixture.AddJobAsync("Second", FulfillmentStatus.Downloading,
            createdMinutesAgo: 25, claimedMinutesAgo: 10, updatedMinutesAgo: 5);

        var before = await fixture.Service.GetActiveAndRecentAsync();
        Assert.Equal([first.Id, second.Id], before.Select(job => job.JobId));

        first.LastUpdatedAt = DateTime.UtcNow.AddSeconds(1);
        second.LastUpdatedAt = DateTime.UtcNow.AddMinutes(-9);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var after = await fixture.Service.GetActiveAndRecentAsync();
        Assert.Equal([first.Id, second.Id], after.Select(job => job.JobId));
    }

    [Fact]
    public async Task ActiveDownloadsLeadAndDeferredSearchesStayAtBottom()
    {
        await using var fixture = await Fixture.CreateAsync();
        var deferred = await fixture.AddJobAsync("Waiting", FulfillmentStatus.Deferred,
            createdMinutesAgo: 60, claimedMinutesAgo: 55, updatedMinutesAgo: 1);
        var completed = await fixture.AddJobAsync("Completed", FulfillmentStatus.Completed,
            createdMinutesAgo: 50, claimedMinutesAgo: 45, updatedMinutesAgo: 2,
            completedMinutesAgo: 2);
        var queued = await fixture.AddJobAsync("Queued", FulfillmentStatus.Queued,
            createdMinutesAgo: 20, updatedMinutesAgo: 1);
        var laterActive = await fixture.AddJobAsync("Later active", FulfillmentStatus.Claimed,
            createdMinutesAgo: 15, claimedMinutesAgo: 5, updatedMinutesAgo: 5);
        var earlierActive = await fixture.AddJobAsync("Earlier active", FulfillmentStatus.Downloading,
            createdMinutesAgo: 30, claimedMinutesAgo: 10, updatedMinutesAgo: 1);

        var jobs = await fixture.Service.GetActiveAndRecentAsync();

        Assert.Equal(
            [earlierActive.Id, laterActive.Id, queued.Id, completed.Id, deferred.Id],
            jobs.Select(job => job.JobId));
    }

    private sealed class Fixture(SqliteConnection connection, AppDbContext db) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public DownloadMonitorService Service { get; } = new(
            db, new DownloadTelemetryStore(), new StorageTelemetryStore());

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async Task<FulfillmentJobEntity> AddJobAsync(
            string title,
            FulfillmentStatus status,
            int createdMinutesAgo,
            int? claimedMinutesAgo = null,
            int? updatedMinutesAgo = null,
            int? completedMinutesAgo = null)
        {
            var request = new MediaRequestEntity
            {
                MediaId = title.GetHashCode(),
                MediaType = MediaType.TvShow,
                Title = title
            };
            Db.MediaRequests.Add(request);
            await Db.SaveChangesAsync();

            var job = new FulfillmentJobEntity
            {
                MediaRequestId = request.Id,
                MediaId = request.MediaId,
                MediaType = request.MediaType,
                Title = request.Title,
                Status = status,
                CreatedAt = DateTime.UtcNow.AddMinutes(-createdMinutesAgo),
                ClaimedAt = claimedMinutesAgo is int claimed
                    ? DateTime.UtcNow.AddMinutes(-claimed)
                    : null,
                LastUpdatedAt = updatedMinutesAgo is int updated
                    ? DateTime.UtcNow.AddMinutes(-updated)
                    : null,
                CompletedAt = completedMinutesAgo is int completed
                    ? DateTime.UtcNow.AddMinutes(-completed)
                    : null
            };
            Db.FulfillmentJobs.Add(job);
            await Db.SaveChangesAsync();
            return job;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
