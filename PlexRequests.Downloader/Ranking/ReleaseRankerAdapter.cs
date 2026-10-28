using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared;
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

    /// <summary>The planner's actionable explanation when no download could be formed. This includes
    /// coverage/mapping failures that a generic "quality filter" message would hide.</summary>
    string? LastFailureSummary { get; }

    /// <summary>Best-first anime collections rejected only because their outer name cannot prove episode
    /// scope. The pipeline may inspect a bounded number of their manifests against authoritative episode
    /// groups; candidates rejected for quality, language, identity, health, or size never appear here.</summary>
    IReadOnlyList<ReleaseCandidate> ManifestFallbackCandidates { get; }
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
    public string? LastFailureSummary { get; private set; }
    public IReadOnlyList<ReleaseCandidate> ManifestFallbackCandidates { get; private set; } = [];

    public DownloadPlan PlanDownload(IReadOnlyList<ReleaseCandidate> candidates, FulfillmentJobDto job)
    {
        LastFailureSummary = null;
        ManifestFallbackCandidates = [];
        var context = BuildContext(job, relaxFloor: false);
        var ranked = evaluator.EvaluateAll(candidates, job, context);
        // A replacement or a manual grab is something an admin is waiting on, so every candidate it considered is logged.
        if (job.MediaIssueId is not null || job.IsManualGrab) LogCandidateTable(job, ranked);

        // The quality target is a preference, not a dead end — but relaxing it used to happen after a single
        // deferral, because it keyed off a counter that increments on every claim. Now it takes several
        // searches that genuinely found releases and rejected them all, which spans hours rather than
        // half an hour. Routine upgrade jobs never relax: a below-target "upgrade" is a downgrade. An issue
        // replacement does relax, because the copy it replaces is already the problem; the evaluator keeps it at or
        // above that copy's height, so relaxing can't turn a replacement into a downgrade.
        int threshold = Math.Max(1, prefs.Current.RelaxFloorAfterEmptySearches);
        if (!ranked.Any(r => r.Accepted) && (!job.IsUpgrade || job.MediaIssueId is not null) && job.EmptySearchCount >= threshold)
        {
            var relaxed = evaluator.EvaluateAll(candidates, job, BuildContext(job, relaxFloor: true));
            if (relaxed.Any(r => r.Accepted || IsManifestOnlyCandidate(r)))
            {
                logger.LogInformation(
                    "\"{Title}\": nothing at the preferred quality after {Count} empty search(es) — settling for the best available (the upgrade scan will revisit)",
                    job.Title, job.EmptySearchCount);
                ranked = relaxed;
            }
        }

        LastSearchRejectedCandidates = candidates.Count > 0 && !ranked.Any(r => r.Accepted);
        ManifestFallbackCandidates = ranked.Where(IsManifestOnlyCandidate)
            .Select(candidate => candidate.Candidate).ToList();

        var result = planner.Plan(ranked, job, context);
        if (result.IsEmpty)
            result = TryPlanManifestPreflight(ranked, job, context, result);
        result = RequireAnimePackPreflight(result, job);
        if (result.IsEmpty)
            LastFailureSummary = string.Join("; ", result.Notes.Where(note => !string.IsNullOrWhiteSpace(note)).Take(8));
        LogOutcome(job, candidates.Count, ranked, result);
        return result.Plan;
    }

    /// <summary>
    /// One line per candidate the search considered: the verdict, the release, its info hash prefix, where it came from,
    /// its size and seeders, and the reason it was accepted or rejected. This is the answer to "why was link X not picked".
    /// </summary>
    private void LogCandidateTable(FulfillmentJobDto job, IReadOnlyList<RankedCandidate> ranked)
    {
        const int maxLines = 40;
        logger.LogInformation("Job {JobId} \"{Title}\": evaluated {Count} candidate(s), {Accepted} acceptable",
            job.Id, job.Title, ranked.Count, ranked.Count(r => r.Accepted));
        foreach (var r in ranked.Take(maxLines))
        {
            var hash = r.Candidate.Acquisition.SourceId;
            var hashPrefix = string.IsNullOrWhiteSpace(hash) ? "no hash" : hash.Length <= 12 ? hash : hash[..12];
            var why = r.Accepted
                ? $"score {r.Score:F0}"
                : string.Join("; ", r.Rejections.Select(x => x.Detail));
            logger.LogInformation("  {Verdict} {Release} [{Hash}] {Source}, {SizeGb:F1} GB, {Seeders} seeders: {Why}",
                r.Accepted ? "ACCEPT" : "REJECT", r.Candidate.ReleaseName, hashPrefix, r.Candidate.Source,
                r.Candidate.SizeBytes / 1_000_000_000d, r.Candidate.Seeders, why);
        }
        if (ranked.Count > maxLines)
            logger.LogInformation("  ... {More} more candidate(s) not listed", ranked.Count - maxLines);
    }

    private DownloadPlanResult TryPlanManifestPreflight(IReadOnlyList<RankedCandidate> ranked,
        FulfillmentJobDto job, RankingContext context, DownloadPlanResult original)
    {
        if (!job.IsAnime) return original;
        var hasExplicitMap = EpisodeOrderMapping.IsActive(job.EpisodeOrderProfile);
        // An anime issue replacement names the episodes it must replace, so its season is known even when the
        // collection doesn't declare one. Such a collection is provisionally that season's pack. The manifest check
        // below must still map every needed episode before anything downloads.
        var issueSeason = ProvisionalIssueSeason(job);
        var provisional = ranked.Where(candidate => IsManifestOnlyCandidate(candidate)
                && (hasExplicitMap || candidate.LooksLikeCompleteSeries || issueSeason is not null))
            .ToList();
        if (provisional.Count == 0) return original;

        var eligible = provisional.Select(candidate => candidate.Candidate.Acquisition)
            .ToHashSet();
        var preflightRanked = ranked.Select(candidate => provisional.Contains(candidate)
                ? AsProvisional(candidate, hasExplicitMap || candidate.LooksLikeCompleteSeries ? null : issueSeason)
                : candidate)
            .ToList();
        var planned = planner.Plan(preflightRanked, job, context);
        if (planned.IsEmpty || !planned.Plan.CoversAllTargets) return original;

        var items = planned.Plan.Items.Select(item => eligible.Contains(item.Candidate.Acquisition)
                ? item with { RequiresManifestPreflight = true }
                : item)
            .ToList();
        if (!items.Any(item => item.RequiresManifestPreflight)) return original;
        var notes = planned.Notes.Prepend(
            "No ordinary release covered the request; selected an anime collection provisionally for payload-free manifest validation.")
            .ToList();
        return new DownloadPlanResult(planned.Plan with { Items = items }, notes);
    }

    /// <summary>The one season an anime issue replacement is for, when its episodes or season targets name exactly
    /// one. Null for a series-wide or multi-season replacement, where the season of an unscoped collection is unknown.</summary>
    internal static int? ProvisionalIssueSeason(FulfillmentJobDto job)
    {
        if (job.MediaIssueId is null) return null;
        var seasons = job.RequestedEpisodes.Select(e => e.Season)
            .Concat(job.SeasonTargets.Select(t => t.Season))
            .Distinct().ToList();
        return seasons.Count == 1 ? seasons[0] : null;
    }

    /// <summary>Accepts an unscoped collection for manifest preflight. With a season, the collection is treated as that
    /// season's pack, so the planner can take it for the episodes still needed.</summary>
    internal static RankedCandidate AsProvisional(RankedCandidate candidate, int? season) => season is int provisionalSeason
        ? candidate with { Accepted = true, Rejections = [], Season = provisionalSeason, IsPack = true }
        : candidate with { Accepted = true, Rejections = [] };

    private static bool IsManifestOnlyCandidate(RankedCandidate candidate) =>
        candidate.Rejections.Count > 0
        && candidate.Rejections.All(rejection => rejection.Reason == RejectionReason.PackScopeUnknown);

    private static DownloadPlanResult RequireAnimePackPreflight(DownloadPlanResult result, FulfillmentJobDto job)
    {
        if (result.IsEmpty || !job.IsAnime || !result.Plan.Items.Any(item => item.IsPack)) return result;
        if (result.Plan.Items.Any(item => item.IsPack && item.NeededEpisodeRefs is not { Count: > 0 }))
            return DownloadPlanResult.None(
                "anime pack selection has no exact canonical episode target set; metadata must be refreshed before a safe manifest preflight");

        var items = result.Plan.Items.Select(item => item.IsPack
                ? item with { RequiresManifestPreflight = true }
                : item)
            .ToList();
        return new DownloadPlanResult(result.Plan with { Items = items }, result.Notes);
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
        BlocklistedSizes = job.BlocklistedSizes.ToHashSet(),
        RelaxQualityFloor = relaxFloor,
        RelaxedMinimumHeight = job.ReplacedFileHeight ?? 0
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
