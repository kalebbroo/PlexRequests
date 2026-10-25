using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Jobs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

/// <summary>A replacement's library rebuild has to wait for Plex to index the new file, so the run is scheduled, not immediate.</summary>
public sealed class DelayedJobRunTests
{
    [Fact]
    public async Task A_delayed_run_is_due_after_the_delay_and_an_ordinary_run_is_due_now()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.ScheduledJobs.Add(new ScheduledJobEntity
        {
            JobType = JobType.AvailabilityRefresh, Name = "Rescan the Plex library index",
            Enabled = true, IntervalSeconds = 1800, TimeoutSeconds = 3600
        });
        await db.SaveChangesAsync();
        var service = new JobAdminService(db, null!, null!, NullLogger<JobAdminService>.Instance);

        var before = DateTime.UtcNow;
        Assert.True(await service.QueueJobRunAsync(JobType.AvailabilityRefresh, JobAdminService.PlexIndexSettleDelay));
        var delayed = await db.ScheduledJobs.AsNoTracking().SingleAsync(j => j.JobType == JobType.AvailabilityRefresh);
        Assert.InRange(delayed.NextRunAt!.Value, before + JobAdminService.PlexIndexSettleDelay - TimeSpan.FromSeconds(5),
            DateTime.UtcNow + JobAdminService.PlexIndexSettleDelay + TimeSpan.FromSeconds(5));

        Assert.True(await service.QueueJobRunAsync(JobType.AvailabilityRefresh));
        var immediate = await db.ScheduledJobs.AsNoTracking().SingleAsync(j => j.JobType == JobType.AvailabilityRefresh);
        Assert.True(immediate.NextRunAt!.Value <= DateTime.UtcNow.AddSeconds(5));
    }
}
