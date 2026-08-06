using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Jobs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Background;

/// <summary>
/// Keeps the DB-backed Plex availability index fresh: a full Plex scan that upserts item id-maps +
/// per-season episode presence and ages out anything no longer on the server. All availability reads
/// (badges, per-season dedup) come from the DB this fills, so nothing user-facing has to touch Plex live.
///
/// This used to be a <c>BackgroundService</c> with its own <c>Task.Delay</c> loop, invisible to the admin
/// Jobs panel. It's now an <see cref="IJobHandler"/> so it gets run history, enable/disable, an adjustable
/// interval and "Run now" like every other job. The cadence lives in its schedule row, not here.
/// </summary>
public class AvailabilityRefreshService(
    IPlexApiService plex,
    IConfiguration config,
    ILogger<AvailabilityRefreshService> logger) : IJobHandler
{
    public JobType Type => JobType.AvailabilityRefresh;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var plexConfigured = !string.IsNullOrWhiteSpace(config["Plex:PrimaryServerUrl"])
                             && !string.IsNullOrWhiteSpace(config["Plex:ServerToken"]);
        if (!plexConfigured) return JobResult.Skipped("Plex is not configured");

        var result = await plex.RebuildAvailabilityFromPlexAsync(ct);
        logger.LogInformation("Availability refresh pass complete: {@Result}", result);
        return JobResult.Ok(0, result.ToString());
    }
}
