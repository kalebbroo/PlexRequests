using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Ranking;

public interface IReleaseRanker
{
    /// <summary>
    /// Decide what to download for a job: evaluate every candidate, then choose a movie/whole-series single
    /// pick, one or more season packs, or individual episodes. Returns <see cref="DownloadPlan.None"/> when
    /// nothing acceptable is found.
    /// </summary>
    DownloadPlan PlanDownload(IReadOnlyList<ReleaseCandidate> candidates, FulfillmentJobDto job);

    /// <summary>True when the last plan had candidates to work with but rejected all of them — as opposed to
    /// the indexers returning nothing at all. Drives the empty-search counter that relaxes the quality floor.</summary>
    bool LastSearchRejectedCandidates { get; }
}

/// <summary>
/// Downloader-side wrapper over the shared <see cref="ReleaseEvaluator"/> and <see cref="DownloadPlanner"/>.
///
/// The decision logic itself lives in Shared so the web app can run exactly the same evaluation (for
/// interactive search) and so it can be unit-tested without a worker host. What stays here is the part that
/// genuinely belongs to this process: assembling the ranking context from the job payload and local config,
/// and turning the returned data into log output.
///
/// That last part is the fix for a real problem. Rejections used to be emitted one LogDebug line per
/// candidate, which nothing runs at in production — so "why wasn't the 1080p release picked?" had no
/// answer. Now the reasons come back as data and are logged as a single readable summary.
/// </summary>
public class ReleaseRankerAdapter(
    IReleaseEvaluator evaluator,
    IDownloadPlanner planner,
    IDownloadPreferencesProvider prefs,
    IIndexerSettingsProvider indexerSettings,
    ILogger<ReleaseRankerAdapter> logger) : IReleaseRanker
{
    public bool LastSearchRejectedCandidates { get; private set; }

    public DownloadPlan PlanDownload(IReadOnlyList<ReleaseCandidate> candidates, FulfillmentJobDto job)
    {
        var context = BuildContext(job, relaxFloor: false);
        var ranked = evaluator.EvaluateAll(candidates, job, context);

        // The quality target is a preference, not a dead end — but relaxing it used to happen after a single
        // deferral, because it keyed off a counter that increments on every claim. Now it takes several
        // searches that genuinely found releases and rejected them all, which spans hours rather than
        // half an hour. Upgrade jobs never relax: a below-target "upgrade" is a downgrade.
        int threshold = Math.Max(1, prefs.Current.RelaxFloorAfterEmptySearches);
        if (!ranked.Any(r => r.Accepted) && !job.IsUpgrade && job.EmptySearchCount >= threshold)
        {
            var relaxed = evaluator.EvaluateAll(candidates, job, BuildContext(job, relaxFloor: true));
            if (relaxed.Any(r => r.Accepted))
            {
                logger.LogInformation(
                    "\"{Title}\": nothing at the preferred quality after {Count} empty search(es) — settling for the best available (the upgrade scan will revisit)",
                    job.Title, job.EmptySearchCount);
                ranked = relaxed;
            }
        }

        LastSearchRejectedCandidates = candidates.Count > 0 && !ranked.Any(r => r.Accepted);

        var result = planner.Plan(ranked, job, context);
        LogOutcome(job, candidates.Count, ranked, result);
        return result.Plan;
    }

    private RankingContext BuildContext(FulfillmentJobDto job, bool relaxFloor) => new()
    {
        Preferences = prefs.Current.ToDto(),
        Profile = job.QualityProfile,
        Definitions = job.QualityDefinitions,
        CustomFormats = job.CustomFormats,
        CustomFormatScores = job.CustomFormats.ToDictionary(f => f.Id, f => f.Score),
        IndexerPriorities = indexerSettings.All.ToDictionary(i => i.Id, i => i.Priority),
        BlocklistedHashes = job.BlocklistedHashes
            .Select(h => MagnetUtil.Normalize(h))
            .Where(h => h is not null)
            .Select(h => h!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase),
        RelaxQualityFloor = relaxFloor
    };

    private void LogOutcome(FulfillmentJobDto job, int candidateCount, IReadOnlyList<RankedCandidate> ranked, DownloadPlanResult result)
    {
        foreach (var note in result.Notes) logger.LogInformation("\"{Title}\": {Note}", job.Title, note);

        if (!result.IsEmpty) return;

        // One compact line instead of N invisible debug lines: this is what the admin Missing panel's
        // deferral reason should always have been able to say.
        var grouped = ranked.SelectMany(r => r.Rejections.Select(x => x.Reason))
            .GroupBy(x => x)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()}x {g.Key}");
        logger.LogInformation("No acceptable release for \"{Title}\"{Upgrade}: {Count} candidate(s) — {Reasons}",
            job.Title, job.IsUpgrade ? " [upgrade]" : "", candidateCount,
            grouped.Any() ? string.Join(", ", grouped) : "none returned by any indexer");

        // The individual reasons stay available at Debug for the cases where the summary isn't enough.
        foreach (var r in ranked.Where(r => !r.Accepted).Take(50))
            logger.LogDebug("Rejected {Summary}", r.Summary);
    }
}
