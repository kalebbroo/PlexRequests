using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Jobs;

/// <summary>
/// Finds already-available requests that were downloaded below their preferred quality (cutoff unmet) and
/// enqueues an automatic quality-upgrade search for each. The upgrade job re-downloads only the below-target
/// files (specific episodes for TV, the whole title for a movie) at the preferred quality; on success the
/// downloader replaces the old files and the request's achieved quality is recomputed. A per-request cooldown
/// + attempt cap bound the churn so a title whose better release never appears isn't searched forever.
/// </summary>
public class UpgradeScanJob(
    AppDbContext db,
    IFulfillmentQueue queue,
    IQualityRuleService quality,
    IConfiguration config,
    ILogger<UpgradeScanJob> logger) : IJobHandler
{
    public JobType Type => JobType.QualityUpgradeScan;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        if (!config.GetValue<bool>("Fulfillment:Enabled"))
            return JobResult.Skipped("Fulfillment disabled");
        if (!config.GetValue("Upgrades:Enabled", true))
            return JobResult.Skipped("Automatic quality upgrades disabled");

        var now = DateTime.UtcNow;
        var cooldown = TimeSpan.FromHours(Math.Max(1, config.GetValue("Upgrades:CooldownHours", 12)));
        var maxAttempts = Math.Max(1, config.GetValue("Upgrades:MaxAttempts", 5));
        var cutoff = now - cooldown;

        // Candidates: available requests flagged below their preferred quality, off cooldown, under the
        // attempt cap. The CutoffMet flag is maintained by RecomputeAchievedQualityAsync at each fulfillment.
        var candidates = await db.MediaRequests
            .Where(r => r.Status == RequestStatus.Available && !r.CutoffMet
                        && r.UpgradeAttempts < maxAttempts
                        && (r.LastUpgradeSearchAt == null || r.LastUpgradeSearchAt <= cutoff))
            .OrderBy(r => r.LastUpgradeSearchAt)
            .Take(50) // bound the work per pass
            .ToListAsync(ct);

        if (candidates.Count == 0) return JobResult.Skipped("No cutoff-unmet requests are due for an upgrade");

        int enqueued = 0;
        foreach (var req in candidates)
        {
            if (ct.IsCancellationRequested) break;

            // Re-resolve the preferred quality against current admin rules (genres from the enqueue snapshot).
            var genresCsv = await db.FulfillmentJobs.Where(j => j.MediaRequestId == req.Id)
                .OrderByDescending(j => j.Id).Select(j => j.GenresCsv).FirstOrDefaultAsync(ct);
            var genres = string.IsNullOrWhiteSpace(genresCsv) ? null
                : genresCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var target = await quality.ResolveQualityAsync(req.MediaType, req.MediaId, genres);
            int targetHeight = (int)target;

            // Current library files below that target (known resolution only).
            var jobIds = await db.FulfillmentJobs.Where(j => j.MediaRequestId == req.Id)
                .Select(j => j.Id).ToListAsync(ct);
            var belowTarget = await db.ImportedFiles
                .Where(f => jobIds.Contains(f.FulfillmentJobId) && f.FileType == "video"
                            && f.ResolutionHeight > 0 && f.ResolutionHeight < targetHeight)
                .ToListAsync(ct);

            if (belowTarget.Count == 0)
            {
                // Nothing actually below target (e.g. rules changed, or already upgraded) — clear the flag.
                req.CutoffMet = true;
                continue;
            }

            var episodes = belowTarget
                .Where(f => f.SeasonNumber is not null && f.EpisodeNumber is not null)
                .Select(f => (f.SeasonNumber!.Value, f.EpisodeNumber!.Value))
                .Distinct().ToList();
            var replacePaths = belowTarget.Select(f => f.DestinationPath)
                .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();

            var dto = new MediaRequestDto
            {
                Id = req.Id, MediaId = req.MediaId, MediaType = req.MediaType, Title = req.Title,
                ExternalId = req.ExternalId, ExternalSource = req.ExternalSource
            };
            var ok = await queue.EnqueueUpgradeAsync(dto, target, replacePaths, episodes);

            // Always advance the cooldown/attempt counter so a request that can't be enqueued (e.g. an
            // upgrade already in flight) isn't reconsidered every single pass.
            req.LastUpgradeSearchAt = now;
            if (ok)
            {
                req.UpgradeAttempts++;
                enqueued++;
                logger.LogInformation("Upgrade queued for \"{Title}\" (#{Id}): have {Have}, want {Want}, {Files} file(s) below target",
                    req.Title, req.Id, req.AchievedQuality, target, belowTarget.Count);
            }
        }
        await db.SaveChangesAsync(ct);

        return enqueued > 0
            ? JobResult.Ok(enqueued, $"Enqueued {enqueued} quality upgrade(s)")
            : JobResult.Skipped("No upgrades enqueued this pass");
    }
}
