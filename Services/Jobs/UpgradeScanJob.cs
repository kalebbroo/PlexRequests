using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
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
    IQualityProfileService profiles,
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

        // Candidates: available requests known to be below their preferred quality, off cooldown, under the
        // attempt cap. CutoffState is maintained by RecomputeAchievedQualityAsync at each fulfillment.
        var candidates = await db.MediaRequests
            // Unknown is included deliberately. The tri-state was introduced so an import with no parsed
            // resolution could not be silently declared satisfied — but the scan then only ever looked at
            // Unmet, so Unknown became a quieter version of the same dead end: never claimed met, never
            // upgraded, invisible forever. On the live box that is 8 available requests, including the
            // 720p House of the Dragon episodes that have plenty of 1080p releases available.
            //
            // Evaluating an Unknown request is cheap and self-correcting: TryEnqueueUpgradeAsync resolves
            // it to Met or Unmet the moment any file's resolution is known, and leaves it Unknown otherwise.
            // Honour the per-series toggle. It was stored, shown in the monitoring panel, read back for
            // display — and consulted by nothing that performs upgrades, so switching it on did precisely
            // nothing. A user who set "search for better versions" and watched 720p episodes sit there was
            // observing exactly that. It defaults to true, so wiring it in disables nothing that was working.
            .Where(r => r.Status == RequestStatus.Available
                        && r.SearchForCutoffUpgrades
                        && (r.CutoffState == CutoffState.Unmet || r.CutoffState == CutoffState.Unknown)
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
            if (await TryEnqueueUpgradeAsync(req, now, ct)) enqueued++;
        }
        await db.SaveChangesAsync(ct);

        return enqueued > 0
            ? JobResult.Ok(enqueued, $"Enqueued {enqueued} quality upgrade(s)")
            : JobResult.Skipped("No upgrades enqueued this pass");
    }

    /// <summary>
    /// Consider one request for an upgrade and enqueue it if there's genuinely something below target.
    /// Mutates <paramref name="req"/> but does not save — callers batch that. Shared with the admin
    /// "Upgrade now" action, which used to carry a near-verbatim copy of this logic with subtly different
    /// guards (it ignored the attempt cap and never resolved the cutoff state), so the two could disagree
    /// about the same request.
    /// </summary>
    public async Task<bool> TryEnqueueUpgradeAsync(MediaRequestEntity req, DateTime now, CancellationToken ct)
    {
        // Target = the cutoff of the request's profile, re-read every scan so editing the profile re-flags
        // what needs upgrading. A request predating profiles gets one resolved for it now.
        int profileId = req.QualityProfileId ?? 0;
        if (profileId == 0)
        {
            var genresCsv = await db.FulfillmentJobs.Where(j => j.MediaRequestId == req.Id)
                .OrderByDescending(j => j.Id).Select(j => j.GenresCsv).FirstOrDefaultAsync(ct);
            var genres = string.IsNullOrWhiteSpace(genresCsv) ? null
                : genresCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            profileId = await profiles.ResolveProfileIdAsync(req.MediaType, req.MediaId, genres,
                requesterChoiceId: null, userId: req.RequestedByUserId);
            req.QualityProfileId = profileId;
        }

        // A profile with upgrades switched off means "take what you got and stop" — respect that before
        // doing any work.
        var profile = await profiles.GetProfileAsync(profileId);
        if (profile is { UpgradeAllowed: false }) return false;

        var target = await profiles.GetCutoffQualityAsync(profileId);
        int targetHeight = (int)target;

        var jobIds = await db.FulfillmentJobs.Where(j => j.MediaRequestId == req.Id)
            .Select(j => j.Id).ToListAsync(ct);
        var videoFiles = await db.ImportedFiles
            .Where(f => jobIds.Contains(f.FulfillmentJobId) && f.FileType == "video")
            .ToListAsync(ct);
        var belowTarget = videoFiles.Where(f => f.ResolutionHeight > 0 && f.ResolutionHeight < targetHeight).ToList();

        if (belowTarget.Count == 0)
        {
            // Nothing is *known* to be below target — but that's two very different situations, and the old
            // code collapsed them by setting CutoffMet = true for both. If no file has a parsed resolution we
            // simply don't know, and declaring the cutoff met there permanently disqualified the request from
            // ever being upgraded. Only claim Met when we actually have resolutions to judge.
            bool anyKnownResolution = videoFiles.Any(f => f.ResolutionHeight > 0);
            req.CutoffState = anyKnownResolution ? CutoffState.Met : CutoffState.Unknown;
            req.CutoffMet = req.CutoffState == CutoffState.Met;
            if (!anyKnownResolution)
                logger.LogDebug("Upgrade scan: \"{Title}\" (#{Id}) has {Count} imported file(s) with no known resolution; leaving the cutoff state Unknown",
                    req.Title, req.Id, videoFiles.Count);
            return false;
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
            logger.LogInformation("Upgrade queued for \"{Title}\" (#{Id}): have {Have}, want {Want}, {Files} file(s) below target",
                req.Title, req.Id, req.AchievedQuality, target, belowTarget.Count);
        }
        return ok;
    }
}
