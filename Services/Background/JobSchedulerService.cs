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
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<JobSchedulerService> logger) : BackgroundService
{
    // How often we look for due jobs. Kept short so an admin "Run now" feels responsive; the actual job
    // cadence is governed per-job by IntervalSeconds, not by this tick.
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    private const int JobRunHistoryToKeep = 200;

    // Due jobs dispatch concurrently. They used to run one after another inside a single tick, which meant a
    // slow job (the Plex availability scan can take minutes) held up every other due job behind it. Kept low
    // because these all contend for one SQLite file; WAL + busy_timeout absorb that, a big fan-out wouldn't.
    private const int MaxConcurrentJobs = 3;

    // Floor on any reschedule, including a handler-supplied NextRunAtOverride. Sub-minute wake precision isn't
    // available from a 15s ticker and nothing here needs it.
    private const int MinRescheduleSeconds = 60;

    // Built-in schedule seeded on first run: (type, display name, default interval seconds, timeout seconds).
    // The last four carry the cadences they used as hardcoded BackgroundService loops, so absorbing them into
    // the engine is behaviour-preserving. The availability scan gets a long timeout — it walks the whole Plex
    // library and legitimately takes minutes on a big one.
    private static readonly (JobType Type, string Name, int DefaultInterval, int Timeout)[] BuiltIns =
    {
        (JobType.MissingSearch, "Search for missing releases", 30 * 60, 30 * 60),
        (JobType.QualityUpgradeScan, "Scan for quality upgrades", 6 * 60 * 60, 30 * 60),
        (JobType.StaleClaimReaper, "Recover stalled downloads", 5 * 60, 10 * 60),
        (JobType.AvailabilityRefresh, "Rescan the Plex library index", 30 * 60, 60 * 60),
        (JobType.AvailabilityReconcile, "Match requests against Plex", 30 * 60, 30 * 60),
        // Replaces the old blind 6-hourly series poll. CalendarRefresh keeps air dates current; AirDateMonitor
        // wakes when an episode is actually due, steering its own next run rather than ticking on a fixed cycle.
        (JobType.CalendarRefresh, "Refresh the air-date calendar", 6 * 60 * 60, 30 * 60),
        (JobType.AirDateMonitor, "Fetch newly-aired episodes", 15 * 60, 15 * 60),
        // Interactive-search results are serialized into their row, so this table has to be swept or it
        // grows without bound. Frequent and cheap.
        (JobType.SearchTaskCleanup, "Clean up expired searches and blocks", 10 * 60, 5 * 60),
        // Daily, and cheap: it reads stored release names and touches nothing outside the database. The
        // point is that editing a custom format eventually applies to the library you already have, rather
        // than only to the next thing downloaded. "Run now" is there for when you want it immediately.
        (JobType.RecomputeFormatScores, "Re-score the existing library", 24 * 60 * 60, 30 * 60)
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

    /// <summary>
    /// Clear state left behind by a process that died mid-run. <see cref="ScheduledJobEntity.IsRunning"/> is set
    /// before dispatch and cleared after, so a crash, container restart or redeploy in between used to leave the
    /// flag set forever — and both the due-query and the re-entry guard skip a row whose flag is set, so that job
    /// type never ran again. The per-run try/finally prevents new occurrences; this repairs rows already stuck.
    /// </summary>
    private async Task ResetInterruptedRunsAsync(AppDbContext db, CancellationToken ct)
    {
        var wedged = await db.ScheduledJobs.Where(j => j.IsRunning).Select(j => j.JobType).ToListAsync(ct);
        if (wedged.Count > 0)
        {
            await db.ScheduledJobs.Where(j => j.IsRunning).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.IsRunning, false)
                .SetProperty(j => j.RunningSince, (DateTime?)null), ct);
            logger.LogWarning("Cleared a stuck running flag on {Count} job(s) left over from a previous process: {Types}",
                wedged.Count, string.Join(", ", wedged));
        }

        var orphaned = await db.JobRuns.Where(r => r.Status == JobRunStatus.Running).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, JobRunStatus.Failed)
            .SetProperty(r => r.FinishedAt, DateTime.UtcNow)
            .SetProperty(r => r.Message, "Interrupted by an application restart"), ct);
        if (orphaned > 0)
            logger.LogWarning("Closed {Count} job run(s) left open by a previous process", orphaned);
    }

    private async Task SeedScheduleAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        await ResetInterruptedRunsAsync(db, ct);

        var existing = await db.ScheduledJobs.Select(j => j.JobType).ToListAsync(ct);
        var now = DateTime.UtcNow;
        int seeded = 0;
        foreach (var b in BuiltIns)
        {
            if (existing.Contains(b.Type)) continue;
            db.ScheduledJobs.Add(new ScheduledJobEntity
            {
                JobType = b.Type,
                Name = b.Name,
                Enabled = true,
                IntervalSeconds = b.DefaultInterval,
                TimeoutSeconds = b.Timeout,
                // Run shortly after startup rather than a full interval out — the services these replaced all
                // ran a first pass within ~a minute of boot, and waiting 6h for the first availability scan on
                // a fresh install would leave the whole app looking broken. Staggered so they don't all
                // contend for the same SQLite file at once.
                NextRunAt = now.AddSeconds(45 + seeded * 30),
                CreatedAt = now
            });
            seeded++;
        }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        if (seeded > 0) logger.LogInformation("Seeded {Count} new job schedule row(s)", seeded);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Snapshot the ids of due jobs, then let the context go — each run opens its own.
        List<int> dueIds;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            dueIds = await db.ScheduledJobs
                .Where(j => j.Enabled && !j.IsRunning && (j.NextRunAt == null || j.NextRunAt <= now))
                .Select(j => j.Id)
                .ToListAsync(ct);
        }
        if (dueIds.Count == 0) return;

        // Concurrent, but bounded. Each id appears once per tick and RunOneAsync re-checks IsRunning under its
        // own scope, so a job can't be double-dispatched either within a tick or across overlapping ticks.
        // Per-job exceptions are swallowed here on purpose: letting one escape would cancel its siblings.
        await Parallel.ForEachAsync(dueIds,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentJobs, CancellationToken = ct },
            async (id, token) =>
            {
                try { await RunOneAsync(id, token); }
                catch (OperationCanceledException) { /* shutdown; the run already recorded itself */ }
                catch (Exception ex) { logger.LogError(ex, "Scheduled job {ScheduleId} failed outside its handler", id); }
            });
    }

    private async Task RunOneAsync(int scheduleId, CancellationToken ct)
    {
        // Bookkeeping gets its OWN context, deliberately not the handler's. AppDbContext is scoped and
        // resolved from the same factory, so sharing the scope would hand the handler and the scheduler the
        // same instance — and FinalizeRunAsync's save (which must run even on cancellation) would then commit
        // whatever the handler had left tracked and unsaved. The availability scan mutates thousands of rows
        // in place, so that would mean committing a half-finished library scan on shutdown.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        using var scope = scopeFactory.CreateScope();

        var schedule = await db.ScheduledJobs.FirstOrDefaultAsync(j => j.Id == scheduleId, ct);
        if (schedule is null || !schedule.Enabled || schedule.IsRunning) return;

        bool manual = schedule.ManualRunRequested;
        var startedAt = DateTime.UtcNow;
        schedule.IsRunning = true;
        schedule.RunningSince = startedAt;
        schedule.ManualRunRequested = false;
        var run = new JobRunEntity
        {
            JobType = schedule.JobType,
            ScheduledJobId = schedule.Id,
            StartedAt = startedAt,
            Status = JobRunStatus.Running,
            TriggeredManually = manual
        };
        db.JobRuns.Add(run);
        await db.SaveChangesAsync(ct);

        // Cap a single run so one hung handler can't hold its dispatch slot forever. This only cancels the
        // token — a handler that doesn't observe it is abandoned rather than stopped, but the flag still clears.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(MinRescheduleSeconds, schedule.TimeoutSeconds)));

        var result = JobResult.Failed("Job did not complete");
        try
        {
            var handler = scope.ServiceProvider.GetServices<IJobHandler>().FirstOrDefault(h => h.Type == schedule.JobType);
            if (handler is null)
                result = JobResult.Failed($"No handler registered for {schedule.JobType}");
            else
                result = await handler.ExecuteAsync(new JobContext(schedule, manual), timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The job's own timeout fired, not app shutdown.
            result = JobResult.Failed($"Timed out after {schedule.TimeoutSeconds}s");
            logger.LogWarning("Job {JobType} timed out after {Timeout}s", schedule.JobType, schedule.TimeoutSeconds);
        }
        catch (OperationCanceledException)
        {
            // App shutdown. Record it, then let the finally clean up and the exception propagate.
            result = JobResult.Failed("Interrupted by shutdown");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobType} threw", schedule.JobType);
            result = JobResult.Failed(ex.Message);
        }
        finally
        {
            // Runs on every path including shutdown cancellation — this is what stops a job type wedging.
            await FinalizeRunAsync(db, schedule, run, result, startedAt);
        }

        await PruneHistoryAsync(db, schedule.JobType, ct);
    }

    /// <summary>
    /// Write the run outcome and reschedule. Deliberately uses <see cref="CancellationToken.None"/>: this is
    /// called from a finally that also runs during shutdown, and saving with the cancelled token would throw
    /// there — leaving <see cref="ScheduledJobEntity.IsRunning"/> set, which is the exact failure being fixed.
    /// </summary>
    private async Task FinalizeRunAsync(AppDbContext db, ScheduledJobEntity schedule, JobRunEntity run, JobResult result, DateTime startedAt)
    {
        try
        {
            var now = DateTime.UtcNow;
            var durationMs = (int)Math.Clamp((now - startedAt).TotalMilliseconds, 0, int.MaxValue);

            run.FinishedAt = now;
            run.Status = result.Status;
            run.ItemsProcessed = result.ItemsProcessed;
            run.Message = Trim(result.Message, 2000);

            schedule.IsRunning = false;
            schedule.RunningSince = null;
            schedule.LastRunAt = now;
            schedule.LastStatus = result.Status;
            schedule.LastMessage = Trim(result.Message, 1000);
            schedule.LastRunDurationMs = durationMs;
            schedule.ConsecutiveFailures = result.Status == JobRunStatus.Failed ? schedule.ConsecutiveFailures + 1 : 0;

            var interval = Math.Max(MinRescheduleSeconds, schedule.IntervalSeconds);
            schedule.NextRunAt = result.NextRunAtOverride is DateTime requested
                ? ClampNextRun(requested, now, interval)
                : now.AddSeconds(interval);

            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Never let bookkeeping mask the real outcome. The startup reset covers whatever is left behind.
            logger.LogError(ex, "Could not record the outcome of job {JobType}", schedule.JobType);
        }
    }

    /// <summary>
    /// A handler may pull its next run in (it knows when its work actually falls due) but may not push it past
    /// one normal interval — otherwise a buggy handler could park itself indefinitely — nor below the 60s floor,
    /// which would busy-loop the ticker.
    /// </summary>
    private static DateTime ClampNextRun(DateTime requested, DateTime now, int intervalSeconds)
    {
        var earliest = now.AddSeconds(MinRescheduleSeconds);
        var latest = now.AddSeconds(intervalSeconds);
        if (requested < earliest) return earliest;
        return requested > latest ? latest : requested;
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
