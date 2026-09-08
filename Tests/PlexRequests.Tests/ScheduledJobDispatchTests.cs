using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Background;
using PlexRequestsHosted.Services.Jobs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class ScheduledJobDispatchTests
{
    [Fact]
    public async Task System_trigger_queues_run_without_marking_it_as_manual()
    {
        await using var fixture = await JobDb.CreateAsync();
        var schedule = fixture.AddAvailabilityRefresh(isRunning: true, nextRunAt: null);
        await fixture.Db.SaveChangesAsync();
        var service = new JobAdminService(
            fixture.Db, null!, null!, NullLogger<JobAdminService>.Instance);

        var queued = await service.QueueJobRunAsync(JobType.AvailabilityRefresh);

        Assert.True(queued);
        Assert.NotNull(schedule.NextRunAt);
        Assert.True(schedule.NextRunAt <= DateTime.UtcNow);
        Assert.False(schedule.ManualRunRequested);
    }

    [Fact]
    public async Task Coalesced_system_trigger_does_not_clear_pending_admin_attribution()
    {
        await using var fixture = await JobDb.CreateAsync();
        var schedule = fixture.AddAvailabilityRefresh(isRunning: false, nextRunAt: DateTime.UtcNow.AddHours(1));
        await fixture.Db.SaveChangesAsync();
        var service = new JobAdminService(
            fixture.Db, null!, null!, NullLogger<JobAdminService>.Instance);

        Assert.True(await service.RunJobNowAsync(JobType.AvailabilityRefresh));
        Assert.True(await service.QueueJobRunAsync(JobType.AvailabilityRefresh));

        Assert.True(schedule.ManualRunRequested);
        Assert.True(schedule.NextRunAt <= DateTime.UtcNow);
    }

    [Fact]
    public async Task Finalization_preserves_a_follow_up_requested_during_the_active_run()
    {
        await using var fixture = await JobDb.CreateAsync();
        var pendingFollowUp = DateTime.UtcNow.AddSeconds(-1);
        var schedule = fixture.AddAvailabilityRefresh(isRunning: true, nextRunAt: pendingFollowUp);
        await fixture.Db.SaveChangesAsync();
        var normalNextRun = DateTime.UtcNow.AddMinutes(30);

        await JobSchedulerService.FinalizeScheduleAsync(
            fixture.Db, schedule.Id, JobResult.Ok(1), DateTime.UtcNow, 250, normalNextRun);

        await fixture.Db.Entry(schedule).ReloadAsync();
        Assert.Equal(pendingFollowUp, schedule.NextRunAt);
        Assert.False(schedule.IsRunning);
        Assert.Equal(250, schedule.LastRunDurationMs);
    }

    [Fact]
    public async Task Finalization_uses_normal_cadence_when_no_follow_up_is_pending()
    {
        await using var fixture = await JobDb.CreateAsync();
        var schedule = fixture.AddAvailabilityRefresh(isRunning: true, nextRunAt: null);
        await fixture.Db.SaveChangesAsync();
        var normalNextRun = DateTime.UtcNow.AddMinutes(30);

        await JobSchedulerService.FinalizeScheduleAsync(
            fixture.Db, schedule.Id, JobResult.Skipped("nothing changed"), DateTime.UtcNow, 100, normalNextRun);

        await fixture.Db.Entry(schedule).ReloadAsync();
        Assert.Equal(normalNextRun, schedule.NextRunAt);
        Assert.Equal(JobRunStatus.Skipped, schedule.LastStatus);
    }

    private sealed class JobDb : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public AppDbContext Db { get; }

        private JobDb(SqliteConnection connection, AppDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public static async Task<JobDb> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new JobDb(connection, db);
        }

        public ScheduledJobEntity AddAvailabilityRefresh(bool isRunning, DateTime? nextRunAt)
        {
            var schedule = new ScheduledJobEntity
            {
                JobType = JobType.AvailabilityRefresh,
                Name = "Rescan the Plex library index",
                Enabled = true,
                IntervalSeconds = 1800,
                TimeoutSeconds = 3600,
                IsRunning = isRunning,
                RunningSince = isRunning ? DateTime.UtcNow : null,
                NextRunAt = nextRunAt
            };
            Db.ScheduledJobs.Add(schedule);
            return schedule;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
