using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Jobs;

/// <summary>
/// Re-search handler for the "never dead-end" flow. Requests the downloader couldn't find a release for are
/// parked as <see cref="FulfillmentStatus.Deferred"/> with a <c>NextRetryAt</c> backoff (see
/// <see cref="RetryBackoff"/>). This job flips every deferred job whose backoff has elapsed back to
/// <see cref="FulfillmentStatus.Queued"/>, so the existing worker → indexer → ranker path searches it again.
/// If it's still not found, the downloader re-defers it (bumping the backoff) — it never becomes a failure.
/// </summary>
public class MissingSearchJob(AppDbContext db, ILogger<MissingSearchJob> logger) : IJobHandler
{
    public JobType Type => JobType.MissingSearch;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Deferred, non-upgrade jobs whose retry time has passed and whose request still wants content
        // (not cancelled/rejected/already available). Upgrade jobs are terminal on empty — never re-queued here.
        var due = await db.FulfillmentJobs
            .Where(j => j.Status == FulfillmentStatus.Deferred && !j.IsUpgrade
                        && (j.NextRetryAt == null || j.NextRetryAt <= now)
                        && j.MediaRequest != null
                        && j.MediaRequest.Status != RequestStatus.Cancelled
                        && j.MediaRequest.Status != RequestStatus.Rejected
                        && j.MediaRequest.Status != RequestStatus.Available)
            .ToListAsync(ct);

        if (due.Count == 0) return JobResult.Skipped("No deferred requests are due for re-search");

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
