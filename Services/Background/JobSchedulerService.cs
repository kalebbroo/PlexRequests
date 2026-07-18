using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Jobs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Background;

/// <summary>
/// The generic recurring-job engine. Ticks on a short interval; for every enabled <see cref="ScheduledJobEntity"/>
/// whose <c>NextRunAt</c> has elapsed it dispatches the matching <see cref="IJobHandler"/> in a fresh DI scope,
/// records a <see cref="JobRunEntity"/> for the history feed, and reschedules. Seeds the built-in job schedule
/// (MissingSearch, QualityUpgradeScan) on startup. This is the single place background jobs are orchestrated —
/// individual jobs contain no timing logic of their own.
/// </summary>
public class JobSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<JobSchedulerService> logger) : BackgroundService
{
    // How often we look for due jobs. Kept short so an admin "Run now" feels responsive; the actual job
    // cadence is governed per-job by IntervalSeconds, not by this tick.
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    private const int JobRunHistoryToKeep = 200;

    // Built-in schedule seeded on first run: (type, display name, default interval seconds).
    private static readonly (JobType Type, string Name, int DefaultInterval)[] BuiltIns =
    {
        (JobType.MissingSearch, "Search for missing releases", 30 * 60),      // every 30 min
        (JobType.QualityUpgradeScan, "Scan for quality upgrades", 6 * 60 * 60) // every 6 hours
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } // let the app finish starting
        catch (OperationCanceledException) { return; }

        try { await SeedScheduleAsync(stoppingToken); }
        catch (Exception ex) { logger.LogError(ex, "Failed to seed the job schedule"); }

        logger.LogInformation("Job scheduler started (tick every {Tick}s)", TickInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Job scheduler tick failed"); }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SeedScheduleAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.ScheduledJobs.Select(j => j.JobType).ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var b in BuiltIns)
        {
            if (existing.Contains(b.Type)) continue;
            db.ScheduledJobs.Add(new ScheduledJobEntity
            {
                JobType = b.Type,
                Name = b.Name,
                Enabled = true,
                IntervalSeconds = b.DefaultInterval,
                NextRunAt = now.AddSeconds(b.DefaultInterval),
                CreatedAt = now
            });
        }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Snapshot the ids of due jobs, then process each in its own scope/transaction.
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dueIds = await db.ScheduledJobs
                .Where(j => j.Enabled && !j.IsRunning && (j.NextRunAt == null || j.NextRunAt <= now))
                .Select(j => j.Id)
                .ToListAsync(ct);

            foreach (var id in dueIds)
            {
                if (ct.IsCancellationRequested) break;
                await RunOneAsync(id, ct);
            }
        }
    }

    private async Task RunOneAsync(int scheduleId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var schedule = await db.ScheduledJobs.FirstOrDefaultAsync(j => j.Id == scheduleId, ct);
        if (schedule is null || !schedule.Enabled || schedule.IsRunning) return;

        bool manual = schedule.ManualRunRequested;
        schedule.IsRunning = true;
        schedule.ManualRunRequested = false;
        var run = new JobRunEntity
        {
            JobType = schedule.JobType,
            ScheduledJobId = schedule.Id,
            StartedAt = DateTime.UtcNow,
            Status = JobRunStatus.Running,
            TriggeredManually = manual
        };
        db.JobRuns.Add(run);
        await db.SaveChangesAsync(ct);

        JobResult result;
        try
        {
            var handler = scope.ServiceProvider.GetServices<IJobHandler>().FirstOrDefault(h => h.Type == schedule.JobType);
            if (handler is null)
                result = JobResult.Failed($"No handler registered for {schedule.JobType}");
            else
                result = await handler.ExecuteAsync(new JobContext(schedule, manual), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobType} threw", schedule.JobType);
            result = JobResult.Failed(ex.Message);
        }

        var now = DateTime.UtcNow;
        run.FinishedAt = now;
        run.Status = result.Status;
        run.ItemsProcessed = result.ItemsProcessed;
        run.Message = Trim(result.Message, 2000);

        schedule.IsRunning = false;
        schedule.LastRunAt = now;
        schedule.LastStatus = result.Status;
        schedule.LastMessage = Trim(result.Message, 1000);
        schedule.NextRunAt = now.AddSeconds(Math.Max(60, schedule.IntervalSeconds));
        await db.SaveChangesAsync(ct);

        await PruneHistoryAsync(db, schedule.JobType, ct);
    }

    // Keep the history feed bounded per job type.
    private static async Task PruneHistoryAsync(AppDbContext db, JobType type, CancellationToken ct)
    {
        var count = await db.JobRuns.CountAsync(r => r.JobType == type, ct);
        if (count <= JobRunHistoryToKeep) return;
        var cutoffId = await db.JobRuns.Where(r => r.JobType == type)
            .OrderByDescending(r => r.Id).Skip(JobRunHistoryToKeep).Select(r => r.Id).FirstOrDefaultAsync(ct);
        if (cutoffId == 0) return;
        await db.JobRuns.Where(r => r.JobType == type && r.Id <= cutoffId).ExecuteDeleteAsync(ct);
    }

    private static string? Trim(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length > max ? s[..max] : s);
}
