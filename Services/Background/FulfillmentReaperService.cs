using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Jobs;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Background;

/// <summary>
/// Backstop for dead workers: periodically finds jobs stuck in Claimed/Downloading with no recent
/// activity and requeues them (another worker retries). After too many attempts a normal job is parked
/// as Deferred on the standard re-search backoff — never hard-failed, because Attempts counts every
/// claim including each re-search of a long-Deferred request, so "many attempts" usually just means
/// "hard-to-find title", not "poison job". Upgrade jobs (content already available) are closed quietly.
/// </summary>
public class FulfillmentReaperService(
    AppDbContext db,
    INotificationService notify,
    IConfiguration config,
    ILogger<FulfillmentReaperService> logger) : IJobHandler
{
    public JobType Type => JobType.StaleClaimReaper;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        if (!config.GetValue<bool>("Fulfillment:Enabled")) return JobResult.Skipped("Fulfillment is disabled");

        var staleMinutes = Math.Max(1, config.GetValue("Fulfillment:StaleMinutes", 30));
        var maxAttempts = Math.Max(1, config.GetValue("Fulfillment:MaxAttempts", 3));

        var acted = await RunOnceAsync(staleMinutes, maxAttempts, ct);
        return acted > 0
            ? JobResult.Ok(acted, $"Reaped {acted} stale job(s)")
            : JobResult.Skipped("No stale downloader claims");
    }

    /// <summary>Run a single reap pass. Returns the number of stale jobs acted on.</summary>
    public async Task<int> RunOnceAsync(int staleMinutes, int maxAttempts, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-staleMinutes);
        var stale = await db.FulfillmentJobs
            .Where(j => (j.Status == FulfillmentStatus.Claimed || j.Status == FulfillmentStatus.Downloading)
                        && (j.LastUpdatedAt == null || j.LastUpdatedAt < cutoff))
            .ToListAsync(ct);

        foreach (var job in stale)
        {
            var req = await db.MediaRequests.FirstOrDefaultAsync(r => r.Id == job.MediaRequestId, ct);

            if (job.Attempts >= maxAttempts && job.IsUpgrade)
            {
                // The content is already available; a stuck upgrade attempt just ends quietly (same
                // semantics as upgrade-exhausted), leaving the request untouched for a later scan.
                job.Status = FulfillmentStatus.Cancelled;
                job.LastError = $"Upgrade made no progress after {job.Attempts} attempt(s); reaper closed it.";
                job.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                logger.LogWarning("Reaped upgrade job {JobId} as Cancelled (>= {Max} attempts)", job.Id, maxAttempts);
            }
            else if (job.Attempts >= maxAttempts)
            {
                // Never dead-end: park it Deferred on the standard backoff so MissingSearch keeps
                // re-searching it (and it stays visible in the admin Missing panel), escalating to
                // admins once instead of permanently failing the request.
                var now = DateTime.UtcNow;
                job.Status = FulfillmentStatus.Deferred;
                job.DeferCount++;
                job.NextRetryAt = RetryBackoff.ComputeNextRetry(job.DeferCount, job.ReleaseDate, now);
                job.LastError = $"No progress after {job.Attempts} attempt(s); parked for re-search.";
                job.ClaimedBy = null;
                job.ClaimedAt = null;
                job.Progress = 0;
                job.LastUpdatedAt = now;
                bool escalate = !job.Escalated && job.DeferCount >= RetryBackoff.EscalateAfterDeferrals;
                if (escalate) job.Escalated = true;
                if (req is not null && req.Status is RequestStatus.Approved or RequestStatus.Processing or RequestStatus.Pending)
                    req.Status = RequestStatus.Searching;
                await db.SaveChangesAsync(ct);
                if (escalate && req is not null) await notify.RequestSearchStalledAsync(ToDto(req), job.DeferCount);
                logger.LogWarning("Reaped job {JobId}: parked Deferred for re-search at {Next:u} (attempt {N}, defer #{Defer})",
                    job.Id, job.NextRetryAt, job.Attempts, job.DeferCount);
            }
            else
            {
                job.Status = FulfillmentStatus.Queued;
                job.ClaimedBy = null;
                job.ClaimedAt = null;
                job.LastUpdatedAt = DateTime.UtcNow;
                if (req is not null && req.Status == RequestStatus.Processing)
                    req.Status = RequestStatus.Approved;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Requeued stale job {JobId} (attempt {N})", job.Id, job.Attempts);
            }
        }

        if (stale.Count > 0) logger.LogInformation("Reaper acted on {Count} stale job(s)", stale.Count);
        return stale.Count;
    }

    private static MediaRequestDto ToDto(MediaRequestEntity r) => new()
    {
        Id = r.Id,
        MediaId = r.MediaId,
        MediaType = r.MediaType,
        Title = r.Title,
        Status = r.Status,
        RequestedByUserId = r.RequestedByUserId ?? 0,
        RequestedByUsername = r.RequestedBy ?? string.Empty,
        DenialReason = r.DenialReason
    };
}
