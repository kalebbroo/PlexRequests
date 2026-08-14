using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Jobs;

/// <summary>
/// Queues searches for episodes whose air window has opened, then tells the scheduler when the next one is
/// due.
///
/// That last part is what turns a fixed-interval ticker into an air-time-accurate queue: the job returns
/// the earliest pending wake time via <c>NextRunAtOverride</c>, so instead of waking every six hours and
/// asking every show whether anything happened, the scheduler sleeps until something actually has. No cron
/// involved — the scheduler already had a next-run column, it just had no way to be steered.
/// </summary>
public class AirDateMonitorJob(
    AppDbContext db,
    IMediaRequestService requests,
    IMonitoringPreferencesService monitoring,
    IConfiguration config,
    ILogger<AirDateMonitorJob> logger) : IJobHandler
{
    public JobType Type => JobType.AirDateMonitor;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        if (!config.GetValue("Monitoring:Enabled", true)) return JobResult.Skipped("Monitoring is disabled");
        if (!config.GetValue<bool>("Fulfillment:Enabled")) return JobResult.Skipped("Fulfillment is disabled");

        var prefs = await monitoring.GetAsync();
        var now = DateTime.UtcNow;

        var due = await db.AirSchedule
            .Where(a => a.Monitored && !a.HasFile && a.MediaRequestId != null
                        && a.NextSearchAt != null && a.NextSearchAt <= now
                        && (a.SearchState == AirSearchState.Due
                            || a.SearchState == AirSearchState.NotDue
                            || a.SearchState == AirSearchState.AirDateUnknown))
            .OrderBy(a => a.NextSearchAt)
            .Take(200)
            .ToListAsync(ct);

        int queued = 0;

        // Group by (request, season) so one season's episodes become ONE request. That matters: a single
        // request is a single downloader job that can grab a season pack and trim it, instead of a doomed
        // job per episode for shows that are only ever packaged as packs.
        foreach (var group in due.GroupBy(a => (RequestId: a.MediaRequestId!.Value, a.SeasonNumber)))
        {
            if (ct.IsCancellationRequested) break;

            var episodes = group.Select(a => (group.Key.SeasonNumber, a.EpisodeNumber)).Distinct().ToList();
            var result = await requests.CreateMonitoredEpisodesAsync(group.Key.RequestId, episodes);

            foreach (var row in group)
            {
                row.LastSearchedAt = now;
                row.SearchAttempts++;
                if (result.Success && result.JobQueued)
                {
                    row.SearchState = AirSearchState.Searching;
                    row.NextSearchAt = null; // the fulfillment pipeline owns it from here
                }
                else
                {
                    // Back off rather than retry immediately — a failure here is usually "already covered
                    // by an active request", which resolves itself.
                    row.NextSearchAt = now.AddHours(Math.Min(24, Math.Pow(2, Math.Min(row.SearchAttempts, 5))));
                }
                row.UpdatedAt = now;
            }

            if (result.Success && result.JobQueued)
            {
                queued += episodes.Count;
                logger.LogInformation("Air-date monitor queued {Count} episode(s) of request #{RequestId} S{Season}",
                    episodes.Count, group.Key.RequestId, group.Key.SeasonNumber);
            }
        }

        await db.SaveChangesAsync(ct);

        // Tell the scheduler when the next episode is actually due, so it can sleep until then.
        var nextDue = await db.AirSchedule
            .Where(a => a.Monitored && !a.HasFile && a.NextSearchAt != null)
            .MinAsync(a => (DateTime?)a.NextSearchAt, ct);

        var result2 = queued > 0
            ? JobResult.Ok(queued, $"Queued {queued} newly-aired episode(s)")
            : JobResult.Skipped(nextDue is DateTime n ? $"Nothing due; next at {n:u}" : "Nothing scheduled");

        return result2.WithNextRun(nextDue);
    }
}
