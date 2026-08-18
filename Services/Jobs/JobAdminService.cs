using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Jobs;

/// <summary>
/// Backs the admin "Jobs" panel: reads the schedule, run history, the "Missing" (still-searching) list and
/// the "Cutoff Unmet" (below-preferred-quality) list, and performs the panel's actions. Actions manipulate
/// the same <c>FulfillmentJob</c>/<c>ScheduledJob</c> rows the scheduler and downloader use, so the UI and
/// the background engine stay consistent.
/// </summary>
public class JobAdminService(
    AppDbContext db,
    IQualityProfileService profiles,
    UpgradeScanJob upgradeScan,
    ILogger<JobAdminService> logger) : IJobAdminService
{
    // ---- Scheduled jobs -----------------------------------------------------------------------------

    public async Task<List<ScheduledJobDto>> GetScheduledJobsAsync() =>
        await db.ScheduledJobs.OrderBy(j => j.JobType).Select(j => new ScheduledJobDto
        {
            Id = j.Id, JobType = j.JobType, Name = j.Name, Enabled = j.Enabled,
            IntervalSeconds = j.IntervalSeconds, NextRunAt = j.NextRunAt, LastRunAt = j.LastRunAt,
            LastStatus = j.LastStatus, LastMessage = j.LastMessage, IsRunning = j.IsRunning,
            RunningSince = j.RunningSince, TimeoutSeconds = j.TimeoutSeconds,
            LastRunDurationMs = j.LastRunDurationMs, ConsecutiveFailures = j.ConsecutiveFailures
        }).ToListAsync();

    /// <summary>
    /// Manually clear a stuck <c>IsRunning</c> flag. The scheduler clears it in a finally and again at
    /// startup, so this is a last resort for a flag left by something neither path covered — without it the
    /// only recovery was editing the database by hand, and a set flag stops that job type running at all.
    /// </summary>
    public async Task<bool> ClearRunningFlagAsync(JobType jobType)
    {
        var job = await db.ScheduledJobs.FirstOrDefaultAsync(j => j.JobType == jobType);
        if (job is null || !job.IsRunning) return false;
        job.IsRunning = false;
        job.RunningSince = null;
        job.NextRunAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        logger.LogWarning("Admin cleared a stuck running flag on job {JobType}", jobType);
        return true;
    }

    public async Task<bool> SetJobEnabledAsync(JobType type, bool enabled)
    {
        var j = await db.ScheduledJobs.FirstOrDefaultAsync(x => x.JobType == type);
        if (j is null) return false;
        j.Enabled = enabled;
        // Re-arm the next run when re-enabling so it doesn't fire immediately for a long-overdue NextRunAt.
        if (enabled && (j.NextRunAt == null || j.NextRunAt < DateTime.UtcNow))
            j.NextRunAt = DateTime.UtcNow.AddSeconds(Math.Max(60, j.IntervalSeconds));
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetJobIntervalAsync(JobType type, int intervalSeconds)
    {
        var j = await db.ScheduledJobs.FirstOrDefaultAsync(x => x.JobType == type);
        if (j is null) return false;
        j.IntervalSeconds = Math.Max(60, intervalSeconds);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RunJobNowAsync(JobType type)
    {
        var j = await db.ScheduledJobs.FirstOrDefaultAsync(x => x.JobType == type);
        if (j is null) return false;
        j.NextRunAt = DateTime.UtcNow;   // the scheduler picks it up on its next tick
        j.ManualRunRequested = true;
        await db.SaveChangesAsync();
        return true;
    }

    // ---- History ------------------------------------------------------------------------------------

    public async Task<List<JobRunDto>> GetRecentRunsAsync(int take = 50) =>
        await db.JobRuns.OrderByDescending(r => r.Id).Take(Math.Clamp(take, 1, 500))
            .Select(r => new JobRunDto
            {
                Id = r.Id, JobType = r.JobType, StartedAt = r.StartedAt, FinishedAt = r.FinishedAt,
                Status = r.Status, ItemsProcessed = r.ItemsProcessed, Message = r.Message,
                TriggeredManually = r.TriggeredManually
            }).ToListAsync();

    // ---- Missing (still-searching) ------------------------------------------------------------------

    public async Task<List<WantedItemDto>> GetMissingAsync() =>
        await db.FulfillmentJobs
            .Where(j => j.Status == FulfillmentStatus.Deferred && !j.IsUpgrade && j.MediaRequest != null)
            .OrderByDescending(j => j.Escalated).ThenBy(j => j.NextRetryAt)
            .Select(j => new WantedItemDto
            {
                JobId = j.Id, RequestId = j.MediaRequestId, MediaId = j.MediaId, MediaType = j.MediaType,
                Title = j.Title, Year = j.Year, PosterUrl = j.MediaRequest!.PosterUrl,
                RequestedBy = j.MediaRequest.RequestedBy, TargetQuality = j.Quality,
                DeferCount = j.DeferCount, NextRetryAt = j.NextRetryAt, LastError = j.LastError,
                Escalated = j.Escalated, RequestedAt = j.MediaRequest.RequestedAt
            }).ToListAsync();

    public async Task<bool> SearchMissingNowAsync(int jobId)
    {
        var j = await db.FulfillmentJobs.FirstOrDefaultAsync(x => x.Id == jobId);
        if (j is null || j.Status != FulfillmentStatus.Deferred) return false;
        // Flip straight to Queued so the downloader claims it now, rather than waiting for the scheduler.
        j.Status = FulfillmentStatus.Queued;
        j.NextRetryAt = null;
        j.LastUpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> CancelMissingAsync(int jobId)
    {
        var j = await db.FulfillmentJobs.FirstOrDefaultAsync(x => x.Id == jobId);
        if (j is null) return false;
        j.Status = FulfillmentStatus.Failed;
        j.LastError = "Search cancelled by admin";
        j.CompletedAt = DateTime.UtcNow;
        var req = await db.MediaRequests.FirstOrDefaultAsync(r => r.Id == j.MediaRequestId);
        if (req is not null && req.Status == RequestStatus.Searching)
        {
            req.Status = RequestStatus.Failed;
            req.DenialReason = "Search cancelled by admin";
        }
        await db.SaveChangesAsync();
        logger.LogInformation("Admin cancelled search for request #{RequestId} (job {JobId})", j.MediaRequestId, jobId);
        return true;
    }

    // ---- Cutoff Unmet (upgrade candidates) ----------------------------------------------------------

    public async Task<List<CutoffUnmetItemDto>> GetCutoffUnmetAsync()
    {
        var requests = await db.MediaRequests
            .Where(r => r.Status == RequestStatus.Available && r.CutoffState == CutoffState.Unmet)
            .OrderBy(r => r.LastUpgradeSearchAt)
            .ToListAsync();
        if (requests.Count == 0) return new();

        var ids = requests.Select(r => r.Id).ToList();
        // One lookup for the per-request year (from any of its jobs) and whether an upgrade is in flight.
        var jobInfo = await db.FulfillmentJobs.Where(j => ids.Contains(j.MediaRequestId))
            .Select(j => new { j.MediaRequestId, j.Year, j.GenresCsv, j.IsUpgrade, j.Status, j.Id })
            .ToListAsync();

        var result = new List<CutoffUnmetItemDto>(requests.Count);
        foreach (var r in requests)
        {
            var jobs = jobInfo.Where(j => j.MediaRequestId == r.Id).ToList();
            var year = jobs.OrderByDescending(j => j.Id).Select(j => j.Year).FirstOrDefault(y => y != null);
            var genresCsv = jobs.OrderByDescending(j => j.Id).Select(j => j.GenresCsv).FirstOrDefault();
            var genres = string.IsNullOrWhiteSpace(genresCsv) ? null
                : genresCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var profileId = r.QualityProfileId
                ?? await profiles.ResolveProfileIdAsync(r.MediaType, r.MediaId, genres, null, r.RequestedByUserId);
            var want = await profiles.GetCutoffQualityAsync(profileId);
            var inProgress = jobs.Any(j => j.IsUpgrade
                && j.Status is FulfillmentStatus.Queued or FulfillmentStatus.Claimed
                    or FulfillmentStatus.Downloading or FulfillmentStatus.Deferred);

            result.Add(new CutoffUnmetItemDto
            {
                RequestId = r.Id, MediaId = r.MediaId, MediaType = r.MediaType, Title = r.Title,
                Year = year, PosterUrl = r.PosterUrl, RequestedBy = r.RequestedBy,
                HaveQuality = r.AchievedQuality, WantQuality = want,
                LastUpgradeSearchAt = r.LastUpgradeSearchAt, UpgradeAttempts = r.UpgradeAttempts,
                UpgradeInProgress = inProgress
            });
        }
        return result;
    }

    /// <summary>
    /// Admin "Upgrade now" for a single request. Delegates to the same routine the scheduled scan uses so the
    /// two can't drift — this method previously carried its own copy, which had diverged (no attempt cap, and
    /// it never resolved the cutoff state, so a stale entry stayed in the panel until the scan happened to
    /// clear it). The cooldown is deliberately bypassed here: an admin pressing the button means "now".
    /// </summary>
    public async Task<bool> UpgradeNowAsync(int requestId)
    {
        var req = await db.MediaRequests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (req is null || req.Status != RequestStatus.Available) return false;

        var ok = await upgradeScan.TryEnqueueUpgradeAsync(req, DateTime.UtcNow, CancellationToken.None);
        await db.SaveChangesAsync();
        return ok;
    }
}
