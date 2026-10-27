using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Services.Jobs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

/// <summary>
/// A request may have one job in flight (queued, claimed or downloading) at a time. A parked Deferred job is not in
/// flight, so an approved replacement can be queued beside it, and parked jobs wait for the request to be clear.
/// </summary>
public sealed class ReplacementInFlightTests
{
    [Fact]
    public async Task A_parked_job_does_not_hold_its_requests_slot()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var request = AddRequest(db, 1);
        db.FulfillmentJobs.Add(Job(request, FulfillmentStatus.Deferred));
        db.FulfillmentJobs.Add(Job(request, FulfillmentStatus.Queued));

        await db.SaveChangesAsync();

        Assert.Equal(2, await db.FulfillmentJobs.CountAsync());
    }

    [Fact]
    public async Task Two_jobs_in_flight_on_one_request_are_still_refused()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var request = AddRequest(db, 1);
        db.FulfillmentJobs.Add(Job(request, FulfillmentStatus.Downloading));
        db.FulfillmentJobs.Add(Job(request, FulfillmentStatus.Queued));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_parked_replacement_waits_while_its_request_has_work_in_flight()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        var request = AddRequest(db, 1);
        db.FulfillmentJobs.Add(Job(request, FulfillmentStatus.Downloading));
        var parked = Job(request, FulfillmentStatus.Deferred, mediaIssueId: 1, lastUpdated: now.AddMinutes(-30));
        db.FulfillmentJobs.Add(parked);
        await db.SaveChangesAsync();

        var woken = await ReplacementWake.WakeDeferredAsync(db, now);

        Assert.Equal(0, woken);
        Assert.Equal(FulfillmentStatus.Deferred, (await db.FulfillmentJobs.AsNoTracking().SingleAsync(j => j.Id == parked.Id)).Status);
    }

    [Fact]
    public async Task Only_one_parked_replacement_per_request_wakes_in_a_pass()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        var request = AddRequest(db, 1);
        db.FulfillmentJobs.Add(Job(request, FulfillmentStatus.Deferred, mediaIssueId: 1, lastUpdated: now.AddMinutes(-30)));
        db.FulfillmentJobs.Add(Job(request, FulfillmentStatus.Deferred, mediaIssueId: 2, lastUpdated: now.AddMinutes(-30)));
        await db.SaveChangesAsync();

        var woken = await ReplacementWake.WakeDeferredAsync(db, now);

        Assert.Equal(1, woken);
        Assert.Equal(1, await db.FulfillmentJobs.CountAsync(j => j.Status == FulfillmentStatus.Queued));
    }

    [Fact]
    public void A_parked_monitor_search_widens_its_scope_but_stays_parked_while_another_job_is_in_flight()
    {
        var now = DateTime.UtcNow;
        var child = new MediaRequestEntity
        {
            MediaId = 1, MediaType = MediaType.TvShow, Title = "Title 1", Status = RequestStatus.Approved
        };
        var parked = new FulfillmentJobEntity
        {
            MediaRequestId = 1, MediaId = 1, MediaType = MediaType.TvShow, Title = "Title 1",
            Status = FulfillmentStatus.Deferred, NextRetryAt = now.AddHours(3)
        };

        MediaRequestService.RefreshPendingMonitorScope(child, parked, "S9E4,S9E5", now, requeueNow: false);

        Assert.Equal("S9E4,S9E5", parked.RequestedEpisodesCsv);
        Assert.Equal(FulfillmentStatus.Deferred, parked.Status);
        Assert.Equal(now, parked.NextRetryAt);
    }

    private static MediaRequestEntity AddRequest(AppDbContext db, int mediaId)
    {
        var request = new MediaRequestEntity
        {
            MediaId = mediaId, MediaType = MediaType.Movie, Title = $"Title {mediaId}", Status = RequestStatus.Available
        };
        db.MediaRequests.Add(request);
        db.SaveChanges();
        return request;
    }

    private static FulfillmentJobEntity Job(MediaRequestEntity request, FulfillmentStatus status,
        int? mediaIssueId = null, DateTime? lastUpdated = null) => new()
        {
            MediaRequestId = request.Id, MediaId = request.MediaId, MediaType = MediaType.Movie, Title = request.Title,
            Status = status, IsReplacement = mediaIssueId is not null, IsUpgrade = mediaIssueId is not null,
            MediaIssueId = mediaIssueId, LastUpdatedAt = lastUpdated, NextRetryAt = lastUpdated?.AddHours(1)
        };
}
