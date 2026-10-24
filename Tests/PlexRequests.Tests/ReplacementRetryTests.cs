using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Jobs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class ReplacementRetryTests
{
    [Fact]
    public async Task New_captures_wake_only_issue_replacements_that_have_been_waiting()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        // One active job per request is enforced by the database, so each deferred job needs its own request.
        var waiting = AddDeferred(db, 1, mediaIssueId: 1, lastUpdated: now.AddMinutes(-30));
        var justDeferred = AddDeferred(db, 2, mediaIssueId: 2, lastUpdated: now.AddMinutes(-1));
        // Storage-optimization replacements also carry IsReplacement, but no issue, and keep the ordinary schedule.
        var optimizerReplacement = AddDeferred(db, 3, mediaIssueId: null, lastUpdated: now.AddMinutes(-30), isReplacement: true);
        var ordinary = AddDeferred(db, 4, mediaIssueId: null, lastUpdated: now.AddMinutes(-30));
        await db.SaveChangesAsync();

        var woken = await ReplacementWake.WakeDeferredAsync(db, now);

        Assert.Equal(1, woken);
        Assert.Equal(FulfillmentStatus.Queued, await StatusOf(db, waiting.Id));
        Assert.Equal(FulfillmentStatus.Deferred, await StatusOf(db, justDeferred.Id));
        Assert.Equal(FulfillmentStatus.Deferred, await StatusOf(db, optimizerReplacement.Id));
        Assert.Equal(FulfillmentStatus.Deferred, await StatusOf(db, ordinary.Id));
    }

    [Fact]
    public async Task Wake_applies_the_same_request_and_release_guards_as_the_scheduled_search()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        var cancelled = AddDeferred(db, 1, mediaIssueId: 1, lastUpdated: now.AddMinutes(-30), status: RequestStatus.Cancelled);
        var rejected = AddDeferred(db, 2, mediaIssueId: 2, lastUpdated: now.AddMinutes(-30), status: RequestStatus.Rejected);
        var unreleased = AddDeferred(db, 3, mediaIssueId: 3, lastUpdated: now.AddMinutes(-30), releaseDate: now.AddDays(10));
        var released = AddDeferred(db, 4, mediaIssueId: 4, lastUpdated: now.AddMinutes(-30), releaseDate: now.AddDays(-10));
        await db.SaveChangesAsync();

        var woken = await ReplacementWake.WakeDeferredAsync(db, now);

        Assert.Equal(1, woken);
        Assert.Equal(FulfillmentStatus.Deferred, await StatusOf(db, cancelled.Id));
        Assert.Equal(FulfillmentStatus.Deferred, await StatusOf(db, rejected.Id));
        Assert.Equal(FulfillmentStatus.Deferred, await StatusOf(db, unreleased.Id));
        Assert.Equal(FulfillmentStatus.Queued, await StatusOf(db, released.Id));
    }

    [Fact]
    public void Replacement_retries_are_capped_at_two_hours_while_ordinary_retries_keep_the_daily_ceiling()
    {
        var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(now.AddMinutes(120), RetryBackoff.ComputeNextRetry(10, null, now, RetryBackoff.ReplacementMaxMinutes));
        Assert.Equal(now.AddMinutes(24 * 60), RetryBackoff.ComputeNextRetry(10, null, now, RetryBackoff.OrdinaryMaxMinutes));
    }

    private static async Task<FulfillmentStatus> StatusOf(AppDbContext db, int jobId) =>
        (await db.FulfillmentJobs.AsNoTracking().SingleAsync(j => j.Id == jobId)).Status;

    private static FulfillmentJobEntity AddDeferred(AppDbContext db, int tmdbId, int? mediaIssueId, DateTime lastUpdated,
        bool isReplacement = false, RequestStatus status = RequestStatus.Available, DateTime? releaseDate = null)
    {
        var request = new MediaRequestEntity
        {
            MediaId = tmdbId, MediaType = MediaType.Movie, Title = $"Title {tmdbId}", Status = status
        };
        db.MediaRequests.Add(request);
        db.SaveChanges();
        var job = new FulfillmentJobEntity
        {
            MediaRequestId = request.Id, MediaId = tmdbId, MediaType = MediaType.Movie, Title = request.Title,
            Status = FulfillmentStatus.Deferred, IsReplacement = isReplacement || mediaIssueId is not null,
            IsUpgrade = isReplacement || mediaIssueId is not null, MediaIssueId = mediaIssueId,
            ReleaseDate = releaseDate, LastUpdatedAt = lastUpdated, NextRetryAt = lastUpdated.AddHours(1)
        };
        db.FulfillmentJobs.Add(job);
        return job;
    }
}
