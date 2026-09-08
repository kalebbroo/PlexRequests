using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
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

    [Fact]
    public async Task PlaybackPreparationStatusCountsOnlyLatestUnpreparedMkvDestinations()
    {
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.AddJobAsync("Library", FulfillmentStatus.Completed,
            createdMinutesAgo: 60, completedMinutesAgo: 30);
        fixture.Db.ImportedFiles.AddRange(
            new ImportedFileEntity
            {
                FulfillmentJobId = job.Id, FileType = "video", SourcePath = "/downloads/old.mkv",
                DestinationPath = "/library/duplicate.mkv", PlaybackPreparationAttempts = 3
            },
            new ImportedFileEntity
            {
                FulfillmentJobId = job.Id, FileType = "video", SourcePath = "/downloads/new.mkv",
                DestinationPath = "/library/duplicate.mkv"
            },
            new ImportedFileEntity
            {
                FulfillmentJobId = job.Id, FileType = "video", SourcePath = "/downloads/active.mkv",
                DestinationPath = "/library/active.mkv", PlaybackPreparationClaimedAt = DateTime.UtcNow
            },
            new ImportedFileEntity
            {
                FulfillmentJobId = job.Id, FileType = "video", SourcePath = "/downloads/failed.mkv",
                DestinationPath = "/library/failed.mkv", PlaybackPreparationAttempts = 3
            },
            new ImportedFileEntity
            {
                FulfillmentJobId = job.Id, FileType = "video", SourcePath = "/downloads/recover.mkv",
                DestinationPath = "/library/recover.mkv", PlaybackPreparationAttempts = 3,
                PlaybackPreparationDetail = PlaybackPreparationReportDto.LegacyOutsideRootFailure
            },
            new ImportedFileEntity
            {
                FulfillmentJobId = job.Id, FileType = "video", SourcePath = "/downloads/current.mkv",
                DestinationPath = "/library/current.mkv", PlaybackPreparedAt = DateTime.UtcNow
            },
            new ImportedFileEntity
            {
                FulfillmentJobId = job.Id, FileType = "video", SourcePath = "/downloads/other.mp4",
                DestinationPath = "/library/other.mp4"
            });
        await fixture.Db.SaveChangesAsync();

        var status = await fixture.Service.GetPlaybackPreparationStatusAsync();

        Assert.Equal(2, status.PendingCount);
        Assert.Equal(1, status.InProgressCount);
        Assert.Equal(1, status.FailedCount);
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
