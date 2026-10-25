using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

/// <summary>
/// Every file of a music job is imported, but Plex has not verified the album. The job must close, not sit at 100%
/// Downloading for the reaper to re-run, and the request must stay open for reconciliation.
/// </summary>
public sealed class PendingVerificationTests
{
    [Fact]
    public async Task An_imported_music_job_that_plex_has_not_verified_closes_completed_and_is_not_claimed_again()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var request = new MediaRequestEntity
        {
            MediaId = 0, MediaType = MediaType.Music, Title = "Anne-Marie", Status = RequestStatus.Processing
        };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();
        var job = new FulfillmentJobEntity
        {
            MediaRequestId = request.Id, MediaId = 0, MediaType = MediaType.Music, Title = request.Title,
            Status = FulfillmentStatus.Downloading, Progress = 100, Attempts = 3, ClaimedBy = "worker-1"
        };
        db.FulfillmentJobs.Add(job);
        await db.SaveChangesAsync();
        var queue = new FulfillmentQueue(db, null!, null!, null!, null!, null!, null!,
            NullLogger<FulfillmentQueue>.Instance);

        var closed = await queue.MarkPendingVerificationAsync(job.Id, "Plex album is missing 2 of 10 expected track(s)");

        Assert.True(closed);
        var stored = await db.FulfillmentJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.Equal(FulfillmentStatus.Completed, stored.Status);
        Assert.Equal(100, stored.Progress);
        Assert.Null(stored.ClaimedBy);
        Assert.StartsWith("Imported; Plex verification pending: Plex album is missing 2 of 10", stored.LastError);
        Assert.Equal(RequestStatus.Processing, (await db.MediaRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).Status);

        Assert.Empty(await queue.ClaimNextAsync("worker-2"));
    }

    [Fact]
    public async Task A_late_progress_heartbeat_cannot_reopen_a_completed_job()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var request = new MediaRequestEntity { MediaId = 0, MediaType = MediaType.Music, Title = "Taylor Swift", Status = RequestStatus.Processing };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();
        var job = new FulfillmentJobEntity
        {
            MediaRequestId = request.Id, MediaId = 0, MediaType = MediaType.Music, Title = request.Title,
            Status = FulfillmentStatus.Completed, Progress = 100
        };
        db.FulfillmentJobs.Add(job);
        await db.SaveChangesAsync();
        var queue = new FulfillmentQueue(db, null!, null!, null!, null!, null!, null!,
            NullLogger<FulfillmentQueue>.Instance);

        await queue.ReportProgressAsync(job.Id, 40);

        Assert.Equal(FulfillmentStatus.Completed, (await db.FulfillmentJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id)).Status);
    }

    [Fact]
    public async Task A_progress_report_for_a_running_download_still_marks_it_downloading()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var request = new MediaRequestEntity { MediaId = 603, MediaType = MediaType.Movie, Title = "The Matrix", Status = RequestStatus.Processing };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();
        var job = new FulfillmentJobEntity
        {
            MediaRequestId = request.Id, MediaId = 603, MediaType = MediaType.Movie, Title = request.Title,
            Status = FulfillmentStatus.Queued
        };
        db.FulfillmentJobs.Add(job);
        await db.SaveChangesAsync();
        var queue = new FulfillmentQueue(db, null!, null!, null!, null!, null!, null!,
            NullLogger<FulfillmentQueue>.Instance);

        await queue.ReportProgressAsync(job.Id, 50);

        var stored = await db.FulfillmentJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.Equal(FulfillmentStatus.Downloading, stored.Status);
        Assert.Equal(50, stored.Progress);
    }
}
