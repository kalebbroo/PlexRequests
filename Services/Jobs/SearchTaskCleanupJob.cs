using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Jobs;

/// <summary>
/// Deletes expired interactive-search tasks and lapsed blocklist entries.
///
/// Ships with the writer rather than after it on purpose: a search task carries a serialized result set, so
/// a table that's only ever appended to grows without bound — and the rows are worthless within half an
/// hour of being created. This also closes out tasks nobody ever claimed, which is the visible symptom of
/// the downloader not running.
/// </summary>
public class SearchTaskCleanupJob(
    AppDbContext db,
    IReleaseBlocklistService blocklist,
    ILogger<SearchTaskCleanupJob> logger) : IJobHandler
{
    public JobType Type => JobType.SearchTaskCleanup;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Mark anything that aged out unclaimed, so the UI can say "the downloader never picked this up"
        // rather than spinning forever.
        var abandoned = await db.SearchTasks
            .Where(t => t.ExpiresAt <= now && (t.Status == SearchTaskStatus.Pending || t.Status == SearchTaskStatus.Claimed))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, SearchTaskStatus.Expired)
                .SetProperty(t => t.Error, "The downloader did not pick this search up before it expired"), ct);

        // Then delete everything past its lifetime, including the rows just marked.
        var deleted = await db.SearchTasks.Where(t => t.ExpiresAt <= now.AddMinutes(-5)).ExecuteDeleteAsync(ct);
        var unblocked = await blocklist.PruneExpiredAsync(ct);

        if (abandoned == 0 && deleted == 0 && unblocked == 0)
            return JobResult.Skipped("Nothing to clean up");

        if (abandoned > 0)
            logger.LogWarning("{Count} interactive search(es) expired unclaimed — is the downloader running?", abandoned);

        return JobResult.Ok(deleted + unblocked,
            $"Removed {deleted} expired search task(s) and {unblocked} lapsed blocklist entry(ies)");
    }
}
