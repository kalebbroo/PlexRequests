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
    public async Task New_captures_wake_only_replacements_that_have_been_waiting_not_ones_just_deferred()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        // One active job per request is enforced by the database, so each deferred job needs its own request.
        var waiting = AddDeferred(db, 1, isReplacement: true, lastUpdated: now.AddMinutes(-30));
        var justDeferred = AddDeferred(db, 2, isReplacement: true, lastUpdated: now.AddMinutes(-1));
        var ordinary = AddDeferred(db, 3, isReplacement: false, lastUpdated: now.AddMinutes(-30));
        await db.SaveChangesAsync();

        var woken = await ReplacementWake.WakeDeferredAsync(db, now);

        Assert.Equal(1, woken);
        Assert.Equal(FulfillmentStatus.Queued, (await db.FulfillmentJobs.AsNoTracking().SingleAsync(j => j.Id == waiting.Id)).Status);
        Assert.Equal(FulfillmentStatus.Deferred, (await db.FulfillmentJobs.AsNoTracking().SingleAsync(j => j.Id == justDeferred.Id)).Status);
        Assert.Equal(FulfillmentStatus.Deferred, (await db.FulfillmentJobs.AsNoTracking().SingleAsync(j => j.Id == ordinary.Id)).Status);
    }

    [Fact]
    public void Replacement_retries_are_capped_at_two_hours_while_ordinary_retries_keep_the_daily_ceiling()
    {
        var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(now.AddMinutes(120), RetryBackoff.ComputeNextRetry(10, null, now, RetryBackoff.ReplacementMaxMinutes));
        Assert.Equal(now.AddMinutes(24 * 60), RetryBackoff.ComputeNextRetry(10, null, now));
    }

    private static FulfillmentJobEntity AddDeferred(AppDbContext db, int tmdbId, bool isReplacement, DateTime lastUpdated)
    {
        var request = new MediaRequestEntity
        {
            MediaId = tmdbId, MediaType = MediaType.Movie, Title = $"Title {tmdbId}", Status = RequestStatus.Available
        };
        db.MediaRequests.Add(request);
        db.SaveChanges();
        var job = new FulfillmentJobEntity
        {
            MediaRequestId = request.Id, MediaId = tmdbId, MediaType = MediaType.Movie, Title = request.Title,
            Status = FulfillmentStatus.Deferred, IsReplacement = isReplacement, IsUpgrade = isReplacement,
            LastUpdatedAt = lastUpdated, NextRetryAt = lastUpdated.AddHours(1)
        };
        db.FulfillmentJobs.Add(job);
        return job;
    }
}
