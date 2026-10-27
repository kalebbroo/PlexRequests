using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Jobs;

/// <summary>
/// Puts deferred issue replacements back in the queue as soon as new releases are captured, instead of
/// waiting out their backoff. Replacements are few and an administrator is waiting on each one.
/// </summary>
public static class ReplacementWake
{
    /// <summary>A job deferred this recently has just searched; waking it again would repeat that search.</summary>
    public static readonly TimeSpan MinimumDeferral = TimeSpan.FromMinutes(5);

    public static async Task<int> WakeDeferredAsync(AppDbContext db, DateTime now, CancellationToken ct = default)
    {
        var cutoff = now - MinimumDeferral;
        // Only issue replacements are woken. Storage-optimization jobs also carry IsReplacement, and they keep the
        // ordinary schedule. The request and release-date guards are the ones MissingSearchJob applies, so a wake
        // cannot start a search that the scheduled pass would skip.
        var due = await db.FulfillmentJobs
            .Where(j => j.Status == FulfillmentStatus.Deferred && j.MediaIssueId != null
                        && (j.LastUpdatedAt == null || j.LastUpdatedAt < cutoff)
                        && j.MediaRequest != null
                        && j.MediaRequest.Status != RequestStatus.Cancelled
                        && j.MediaRequest.Status != RequestStatus.Rejected
                        && (j.ReleaseDate == null || j.ReleaseDate <= now))
            .ToListAsync(ct);
        // The database allows one job in flight per request, so skip requests that already have one, and wake at
        // most one parked job per request in a pass.
        var busyRequestIds = (await db.FulfillmentJobs.AsNoTracking()
            .Where(j => FulfillmentJobPolicy.InFlightStatuses.Contains(j.Status))
            .Select(j => j.MediaRequestId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();
        due = due.Where(j => busyRequestIds.Add(j.MediaRequestId)).ToList();
        foreach (var job in due)
        {
            job.Status = FulfillmentStatus.Queued;
            job.NextRetryAt = null;
            job.LastUpdatedAt = now;
        }
        if (due.Count > 0) await db.SaveChangesAsync(ct);
        return due.Count;
    }
}
