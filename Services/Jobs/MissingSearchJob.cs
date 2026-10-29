using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Jobs;

/// <summary>
/// Re-search handler for the "never dead-end" flow. Requests the downloader couldn't find a release for are
/// parked as <see cref="FulfillmentStatus.Deferred"/> with a <c>NextRetryAt</c> backoff (see
/// <see cref="RetryBackoff"/>). This job flips every deferred job whose backoff has elapsed back to
/// <see cref="FulfillmentStatus.Queued"/>, so the existing worker → indexer → ranker path searches it again.
/// If it's still not found, the downloader re-defers it (bumping the backoff) — it never becomes a failure.
/// </summary>
public class MissingSearchJob(AppDbContext db, ILogger<MissingSearchJob> logger, IMediaIssueService issues) : IJobHandler
{
    public JobType Type => JobType.MissingSearch;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Approvals saved while their request had work in flight queue here, once that work has finished.
        var approvedNow = await issues.QueuePendingApprovalsAsync();
        if (approvedNow > 0) logger.LogInformation("Queued {Count} approved replacement(s) that were waiting for their request", approvedNow);

        // Ordinary deferred jobs still require a non-terminal request. Issue replacements are deliberately
        // attached to an Available request (the bad copy remains playable until the new one imports), so they
        // are the one upgrade-shaped job that must pass this gate and keep searching. Routine quality upgrades
        // remain terminal on an empty search and are reconsidered later by UpgradeScan instead.
        var due = await db.FulfillmentJobs
            .Where(j => j.Status == FulfillmentStatus.Deferred && (!j.IsUpgrade || j.IsReplacement)
                        && (j.NextRetryAt == null || j.NextRetryAt <= now)
                        && j.MediaRequest != null
                        && j.MediaRequest.Status != RequestStatus.Cancelled
                        && j.MediaRequest.Status != RequestStatus.Rejected
                        && (j.MediaRequest.Status != RequestStatus.Available || j.IsReplacement))
            .ToListAsync(ct);

        if (due.Count == 0) return JobResult.Skipped("No deferred requests are due for re-search");

        // A request may own one job in flight at a time. A parked job waits while another job for its request is
        // queued, claimed, or downloading, and at most one of several parked jobs for a request is requeued per pass.
        var busyRequestIds = (await db.FulfillmentJobs.AsNoTracking()
            .Where(j => FulfillmentJobPolicy.InFlightStatuses.Contains(j.Status))
            .Select(j => j.MediaRequestId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();
        due = due.Where(j => busyRequestIds.Add(j.MediaRequestId)).ToList();

        if (due.Count == 0) return JobResult.Skipped("Deferred jobs are waiting for their request's current job to finish");

        foreach (var j in due)
        {
            j.Status = FulfillmentStatus.Queued; // the worker's claim query picks it up on its next poll
            j.NextRetryAt = null;
            j.LastUpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation("MissingSearch re-queued {Count} deferred request(s) for another search pass", due.Count);
        return JobResult.Ok(due.Count, $"Re-queued {due.Count} request(s) for search");
    }
}
