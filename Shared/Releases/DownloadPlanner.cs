using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared.Releases;

public interface IDownloadPlanner
{
    /// <summary>Choose what to download from already-evaluated candidates.</summary>
    DownloadPlanResult Plan(IReadOnlyList<RankedCandidate> ranked, FulfillmentJobDto job, RankingContext context);
}

/// <summary>The plan plus why it turned out that way — the notes are what the caller logs or displays.</summary>
public sealed record DownloadPlanResult(DownloadPlan Plan, IReadOnlyList<string> Notes)
{
    public static DownloadPlanResult None(params string[] notes) => new(DownloadPlan.None, notes);
    public bool IsEmpty => Plan.IsEmpty;
}

/// <summary>
/// Turns evaluated candidates into a download plan: a single pick for a movie or whole series, one or more
/// season packs, or individual episodes.
///
/// Split out of the old combined ranker so that judging a release and deciding what to fetch are separate,
/// independently testable steps — and so the web app can evaluate releases (to show an admin why one was
/// rejected) without also planning a download.
/// </summary>
public class DownloadPlanner : IDownloadPlanner
{
    public DownloadPlanResult Plan(IReadOnlyList<RankedCandidate> ranked, FulfillmentJobDto job, RankingContext context)
    {
        var notes = new List<string>();
        var acceptable = ranked.Where(r => r.Accepted).ToList();

        if (acceptable.Count == 0)
        {
            // Summarise WHY nothing was acceptable. The old code logged this per candidate at Debug, which
            // meant that in production the answer to "why did nothing get picked" was simply unavailable.
            var byReason = ranked.SelectMany(r => r.Rejections.Select(x => x.Reason))
                .GroupBy(x => x).OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count()} {Describe(g.Key)}");
            var summary = ranked.Count == 0
                ? "no indexer returned a release"
                : $"{ranked.Count} candidate(s) all rejected: {string.Join(", ", byReason)}";
            return DownloadPlanResult.None(summary);
        }

        // Explicit episode request → one best release per requested (season, episode).
        if (job.RequestedEpisodes.Count > 0)
            return PlanEpisodes(acceptable, job.RequestedEpisodes.Select(e => (e.Season, e.Episode)), job, context, notes);

        var seasonTargets = ResolveSeasonTargets(job);
        if (seasonTargets.Count > 0)
            return PlanSeasons(acceptable, seasonTargets, job, context, notes);

        var best = acceptable.OrderByDescending(a => a.Score).First();
        notes.Add($"Selected \"{best.Candidate.ReleaseName}\" ({best.Resolution}p, {best.Candidate.Seeders} seeders, score {best.Score:F0})");
        return new DownloadPlanResult(new DownloadPlan(DownloadPlanKind.Single, new[] { ToItem(best) }), notes);
    }

    // ---- Season planning ----------------------------------------------------------------------------

    private DownloadPlanResult PlanSeasons(List<RankedCandidate> acceptable, List<SeasonTarget> targets,
        FulfillmentJobDto job, RankingContext context, List<string> notes)
    {
        var p = context.Preferences;
        var items = new List<DownloadPlanItem>();
        bool anyEpisodes = false;
        bool coversAllTargets = true;

        foreach (var target in targets)
        {
            var missing = target.MissingEpisodes;
            var packs = acceptable
                .Where(a => a.IsPack && MatchesSeason(a, target.Season) && CoversEpisodes(a, missing))
                .OrderByDescending(a => a.Score).ToList();
            var bestPack = packs.FirstOrDefault();

            // Report packs that matched the season but demonstrably don't cover what's missing, rather than
            // silently taking one. This is the bug that produced "it only downloaded some episodes".
            var incomplete = acceptable.Where(a => a.IsPack && MatchesSeason(a, target.Season) && !CoversEpisodes(a, missing)).ToList();
            foreach (var inc in incomplete)
                notes.Add($"Skipped \"{inc.Candidate.ReleaseName}\": covers E{inc.EpisodeStart}-E{inc.EpisodeEnd}, need {DescribeEpisodes(missing)}");

            var episodePicks = BuildEpisodePicks(acceptable, target, missing);
            bool wantPack = ShouldPreferPack(p.SeasonPackStrategy, bestPack, episodePicks, missing, notes);

            if (wantPack && bestPack is not null)
            {
                notes.Add($"Season {target.Season}: taking pack \"{bestPack.Candidate.ReleaseName}\" ({bestPack.Resolution}p, score {bestPack.Score:F0})");
                items.Add(ToItem(bestPack) with { Season = target.Season, NeededEpisodes = PackNeeded(target) });
                continue;
            }

            if (!p.AllowEpisodeFallback && p.SeasonPackStrategy != SeasonPackStrategy.PreferEpisodes)
            {
                if (bestPack is not null) { items.Add(ToItem(bestPack) with { Season = target.Season, NeededEpisodes = PackNeeded(target) }); continue; }
                notes.Add($"Season {target.Season}: no acceptable pack and episode fallback is disabled");
                coversAllTargets = false;
                continue;
            }

            if (missing.Count == 0)
            {
                // No episode list to fan out to (a metadata miss) — a pack is the only option.
                if (bestPack is not null) items.Add(ToItem(bestPack) with { Season = target.Season });
                else
                {
                    notes.Add($"Season {target.Season}: no pack and no episode list to fan out to");
                    coversAllTargets = false;
                }
                continue;
            }

            if (missing.Count > p.MaxEpisodesForFanout)
            {
                if (bestPack is not null) { items.Add(ToItem(bestPack) with { Season = target.Season, NeededEpisodes = PackNeeded(target) }); continue; }
                notes.Add($"Season {target.Season}: {missing.Count} episodes missing (over the {p.MaxEpisodesForFanout} fan-out cap) and no usable pack");
                coversAllTargets = false;
                continue;
            }

            var gaps = missing.Where(e => episodePicks.All(x => x.Episode != e)).ToList();
            if (gaps.Count > 0 && bestPack is not null)
            {
                notes.Add($"Season {target.Season}: {gaps.Count} episode(s) have no standalone release — using the pack, trimmed to what's missing");
                items.Add(ToItem(bestPack) with { Season = target.Season, NeededEpisodes = PackNeeded(target) });
                continue;
            }

            if (episodePicks.Count > 0)
            {
                anyEpisodes = true;
                items.AddRange(episodePicks);
                notes.Add($"Season {target.Season}: fanning out to {episodePicks.Count} episode(s)");
                if (gaps.Count > 0)
                {
                    notes.Add($"Season {target.Season}: no release found for episode(s) {string.Join(",", gaps)} — this request will complete partially");
                    coversAllTargets = false;
                }
            }
            else if (gaps.Count > 0) coversAllTargets = false;
        }

        if (items.Count == 0) return DownloadPlanResult.None(notes.ToArray());
        return new DownloadPlanResult(new DownloadPlan(
            anyEpisodes ? DownloadPlanKind.Episodes : DownloadPlanKind.SeasonPack,
            items,
            coversAllTargets), notes);
    }

    private static List<DownloadPlanItem> BuildEpisodePicks(List<RankedCandidate> acceptable, SeasonTarget target, IReadOnlyList<int> missing)
    {
        var picks = new List<DownloadPlanItem>();
        foreach (var ep in missing)
        {
            var best = acceptable
                .Where(a => !a.IsPack && a.Season == target.Season && a.Episode == ep)
                .OrderByDescending(a => a.Score).FirstOrDefault();
            if (best is not null) picks.Add(ToItem(best) with { Season = target.Season, Episode = ep });
        }
        return picks;
    }

    /// <summary>
    /// Whether to take the pack rather than fan out.
    ///
    /// <see cref="SeasonPackStrategy.Auto"/> now differs from PreferPack, which it previously duplicated
    /// exactly: it compares the pack's cost per covered episode against the individual episodes on offer and
    /// only takes the pack when it isn't materially more expensive. That's what "auto" was always documented
    /// to mean.
    /// </summary>
    private static bool ShouldPreferPack(SeasonPackStrategy strategy, RankedCandidate? pack,
        List<DownloadPlanItem> episodePicks, IReadOnlyList<int> missing, List<string> notes)
    {
        if (pack is null) return false;
        if (strategy == SeasonPackStrategy.PreferEpisodes) return false;
        if (strategy == SeasonPackStrategy.PreferPack) return true;

        // Auto: fall back to the pack whenever we can't price the alternative.
        if (episodePicks.Count == 0 || missing.Count == 0) return true;
        if (episodePicks.Count < missing.Count) return true; // episodes alone wouldn't complete the season
        if (!pack.Candidate.SizeKnown || pack.Candidate.SizeGb <= 0) return true;

        var episodeSizes = episodePicks.Where(e => e.Candidate.SizeKnown && e.Candidate.SizeGb > 0)
            .Select(e => e.Candidate.SizeGb).OrderBy(x => x).ToList();
        if (episodeSizes.Count == 0) return true;

        var median = episodeSizes[episodeSizes.Count / 2];
        var perEpisode = pack.Candidate.SizeGb / Math.Max(1, missing.Count);
        bool worthIt = perEpisode <= median * 1.15;
        notes.Add($"Auto: pack is {perEpisode:F2} GB/episode vs {median:F2} GB median standalone — {(worthIt ? "taking the pack" : "fanning out")}");
        return worthIt;
    }

    // ---- Episode planning ---------------------------------------------------------------------------

    private DownloadPlanResult PlanEpisodes(List<RankedCandidate> acceptable, IEnumerable<(int Season, int Episode)> wanted,
        FulfillmentJobDto job, RankingContext context, List<string> notes)
    {
        var p = context.Preferences;
        var items = new List<DownloadPlanItem>();
        bool coversAllTargets = true;

        foreach (var group in wanted.GroupBy(w => w.Season))
        {
            int season = group.Key;
            var episodes = group.Select(w => w.Episode).Distinct().OrderBy(e => e).ToList();
            var bestPack = acceptable
                .Where(a => a.IsPack && MatchesSeason(a, season) && CoversEpisodes(a, episodes))
                .OrderByDescending(a => a.Score).FirstOrDefault();

            if (episodes.Count > p.MaxEpisodesForFanout && bestPack is not null)
            {
                notes.Add($"Season {season}: {episodes.Count} episodes requested (over the fan-out cap) — taking the pack, trimmed");
                items.Add(ToItem(bestPack) with { Season = season, NeededEpisodes = episodes });
                continue;
            }

            var gaps = new List<int>();
            foreach (var ep in episodes)
            {
                var best = acceptable.Where(a => !a.IsPack && a.Season == season && a.Episode == ep)
                    .OrderByDescending(a => a.Score).FirstOrDefault();
                if (best is not null) items.Add(ToItem(best) with { Season = season, Episode = ep });
                else gaps.Add(ep);
            }

            if (gaps.Count == 0) continue;
            if (bestPack is not null)
            {
                notes.Add($"Season {season}: episode(s) {string.Join(",", gaps)} have no standalone release — using the pack, trimmed to them");
                items.Add(ToItem(bestPack) with { Season = season, NeededEpisodes = gaps });
            }
            else
            {
                coversAllTargets = false;
                notes.Add($"Season {season}: no standalone release and no usable pack for episode(s) {string.Join(",", gaps)}");
            }
        }

        return items.Count == 0
            ? DownloadPlanResult.None(notes.ToArray())
            : new DownloadPlanResult(new DownloadPlan(DownloadPlanKind.Episodes, items, coversAllTargets), notes);
    }

    // ---- Matching helpers ---------------------------------------------------------------------------

    /// <summary>A pack matches a season if it names it, the season falls in its multi-season range, or it's
    /// an explicitly-labelled complete-series pack. A release that merely failed to parse a season does NOT
    /// match every season.</summary>
    internal static bool MatchesSeason(RankedCandidate a, int targetSeason)
    {
        if (a.Season is int s)
            return a.SeasonEnd is int end ? targetSeason >= s && targetSeason <= end : s == targetSeason;
        return a.LooksLikeCompleteSeries;
    }

    /// <summary>
    /// Whether a pack can actually satisfy the episodes still missing. Only decides against it when the
    /// release NAME declares a span that demonstrably falls short — an unlabelled pack is presumed complete,
    /// because most season packs don't enumerate their contents and rejecting those would reject nearly
    /// everything. Previously no check existed at all, so an explicit "S01E01-E06" was accepted for a
    /// thirteen-episode season and the request quietly ended up with six.
    /// </summary>
    internal static bool CoversEpisodes(RankedCandidate a, IReadOnlyList<int> missing)
    {
        if (missing.Count == 0) return true;
        if (a.Parsed.EpisodeNumbers.Count > 0)
        {
            var ordered = a.Parsed.EpisodeNumbers.Distinct().OrderBy(x => x).ToList();
            if (!ordered.SequenceEqual(Enumerable.Range(ordered[0], ordered.Count))) return false;
            var declared = ordered.ToHashSet();
            return missing.All(declared.Contains);
        }
        if (a.EpisodeStart is not int start || a.EpisodeEnd is not int end) return true;
        return missing.All(e => e >= start && e <= end);
    }

    private static IReadOnlyList<int>? PackNeeded(SeasonTarget target) =>
        target.MissingEpisodes.Count > 0 ? target.MissingEpisodes : null;

    private static List<SeasonTarget> ResolveSeasonTargets(FulfillmentJobDto job)
    {
        if (job.SeasonTargets.Count > 0) return job.SeasonTargets;
        return job.RequestedSeasons.Select(s => new SeasonTarget { Season = s, EpisodeCount = 0, MissingEpisodes = new() }).ToList();
    }

    private static DownloadPlanItem ToItem(RankedCandidate a) =>
        new(a.Candidate, a.Season, a.Episode, a.IsPack) { Resolution = a.Resolution };

    private static string DescribeEpisodes(IReadOnlyList<int> episodes) =>
        episodes.Count == 0 ? "an unknown set" : $"E{episodes.Min()}-E{episodes.Max()}";

    private static string Describe(RejectionReason reason) => reason switch
    {
        RejectionReason.BelowQualityFloor => "below the quality target",
        RejectionReason.NotInProfile => "not allowed by the quality profile",
        RejectionReason.AboveCutoff => "above the quality target",
        RejectionReason.NotAnUpgrade => "no better than what's already there",
        RejectionReason.TooFewSeeders => "with too few seeders",
        RejectionReason.SizeTooSmall => "too small",
        RejectionReason.SizeTooLarge => "too large",
        RejectionReason.TitleMismatch => "for a different title",
        RejectionReason.ExtraTitleTokens => "for a similarly-named title",
        RejectionReason.ImdbMismatch => "with a mismatched IMDb id",
        RejectionReason.YearMismatch => "from the wrong year",
        RejectionReason.MediaTypeMismatch => "of the wrong media type",
        RejectionReason.PackIncomplete => "incomplete season packs",
        RejectionReason.Blocklisted => "previously failed",
        RejectionReason.TooOld => "too old",
        RejectionReason.MissingAcquisition => "without an acquisition locator",
        _ => reason.ToString()
    };
}
