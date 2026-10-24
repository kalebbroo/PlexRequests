using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
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
        var due = await db.FulfillmentJobs
            .Where(j => j.Status == FulfillmentStatus.Deferred && j.IsReplacement
                        && (j.LastUpdatedAt == null || j.LastUpdatedAt < cutoff))
            .ToListAsync(ct);
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
