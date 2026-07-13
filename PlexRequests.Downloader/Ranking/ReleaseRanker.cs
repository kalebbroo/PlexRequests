using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Ranking;

public interface IReleaseRanker
{
    /// <summary>
    /// Decide what to download for a job: filter to acceptable releases, then choose a movie/whole-series
    /// single pick, one-or-more season packs, or individual episodes (fan-out) per the admin strategy.
    /// Returns <see cref="DownloadPlan.None"/> when nothing acceptable is found.
    /// </summary>
    DownloadPlan PlanDownload(IReadOnlyList<ReleaseCandidate> candidates, FulfillmentJobDto job);
}

/// <summary>
/// Enforces the quality floor + seeder/size thresholds, then scores survivors by resolution, source,
/// seeders, codec efficiency, proper/repack and preferred groups. Season-scoped jobs prefer a full-season
/// pack and fall back to the missing episodes. All knobs come from the admin <see cref="IDownloadPreferencesProvider"/>.
/// </summary>
public class ReleaseRanker(IReleaseParser parser, IDownloadPreferencesProvider prefs, ILogger<ReleaseRanker> logger)
    : IReleaseRanker
{
    private readonly IReleaseParser _parser = parser;
    private readonly IDownloadPreferencesProvider _prefs = prefs;
    private readonly ILogger<ReleaseRanker> _logger = logger;

    private sealed record Annotated(ReleaseCandidate C, int? Season, int? Episode, bool IsPack, double Score, bool Acceptable);

    public DownloadPlan PlanDownload(IReadOnlyList<ReleaseCandidate> candidates, FulfillmentJobDto job)
    {
        var p = _prefs.Current;
        int floor = (int)job.Quality; // Quality enum values are the pixel heights; Any = 0

        var annotated = candidates.Select(c => Annotate(c, job, p)).ToList();
        var acceptable = annotated.Where(a => a.Acceptable).ToList();

        if (acceptable.Count == 0)
        {
            _logger.LogInformation("No acceptable release for \"{Title}\" (of {Total} candidate(s); floor={Floor}, minSeeders={Min})",
                job.Title, candidates.Count, floor, p.MinSeeders);
            return DownloadPlan.None;
        }

        // Explicit episode request → one best release per requested (season, episode).
        if (job.RequestedEpisodes.Count > 0)
            return PlanEpisodes(acceptable, job.RequestedEpisodes.Select(e => (e.Season, e.Episode)), job);

        // Season-scoped request → pack-first with episode fallback, per season.
        var seasonTargets = ResolveSeasonTargets(job);
        if (seasonTargets.Count > 0)
            return PlanSeasons(acceptable, seasonTargets, job, p);

        // Movie / whole-series → single best pick.
        var best = acceptable.OrderByDescending(a => a.Score).First();
        Log("single", best, job);
        return new DownloadPlan(DownloadPlanKind.Single, new[] { ToItem(best) });
    }

    // ---- Season planning ---------------------------------------------------------------------------

    private DownloadPlan PlanSeasons(List<Annotated> acceptable, List<SeasonTarget> targets, FulfillmentJobDto job, EffectiveDownloadPreferences p)
    {
        var items = new List<DownloadPlanItem>();
        bool anyEpisodes = false;

        foreach (var target in targets)
        {
            var packs = acceptable.Where(a => a.IsPack && (a.Season == target.Season || a.Season is null))
                                  .OrderByDescending(a => a.Score).ToList();
            var bestPack = packs.FirstOrDefault();

            bool wantPackFirst = p.SeasonPackStrategy is SeasonPackStrategy.PreferPack or SeasonPackStrategy.Auto;

            if (wantPackFirst && bestPack is not null)
            {
                Log("season-pack", bestPack, job);
                items.Add(ToItem(bestPack) with { Season = target.Season });
                continue;
            }

            // Episode fallback (or PreferEpisodes): assemble the best release per missing episode.
            if (!p.AllowEpisodeFallback && p.SeasonPackStrategy != SeasonPackStrategy.PreferEpisodes)
            {
                if (bestPack is not null) { items.Add(ToItem(bestPack) with { Season = target.Season }); continue; }
                _logger.LogInformation("No acceptable pack for \"{Title}\" S{Season} and episode fallback disabled", job.Title, target.Season);
                continue;
            }

            var missing = target.MissingEpisodes;
            if (missing.Count == 0)
            {
                // No episode list to fan out to (metadata miss) → pack is the only option.
                if (bestPack is not null) { items.Add(ToItem(bestPack) with { Season = target.Season }); }
                else _logger.LogInformation("No pack and no episode list for \"{Title}\" S{Season}; skipping", job.Title, target.Season);
                continue;
            }
            if (missing.Count > p.MaxEpisodesForFanout)
            {
                if (bestPack is not null) { items.Add(ToItem(bestPack) with { Season = target.Season }); continue; }
                _logger.LogWarning("\"{Title}\" S{Season} missing {Count} episodes (> cap {Cap}) and no pack; skipping",
                    job.Title, target.Season, missing.Count, p.MaxEpisodesForFanout);
                continue;
            }

            var episodePicks = new List<DownloadPlanItem>();
            var gaps = new List<int>();
            foreach (var ep in missing)
            {
                var best = acceptable.Where(a => !a.IsPack && a.Season == target.Season && a.Episode == ep)
                                     .OrderByDescending(a => a.Score).FirstOrDefault();
                if (best is not null) episodePicks.Add(ToItem(best) with { Season = target.Season, Episode = ep });
                else gaps.Add(ep);
            }

            // If some episodes have no standalone release, use the pack to cover the whole season instead.
            if (gaps.Count > 0 && bestPack is not null)
            {
                _logger.LogInformation("\"{Title}\" S{Season}: {Gaps} episode(s) had no standalone release → using season pack",
                    job.Title, target.Season, gaps.Count);
                items.Add(ToItem(bestPack) with { Season = target.Season });
                continue;
            }

            if (episodePicks.Count > 0)
            {
                anyEpisodes = true;
                items.AddRange(episodePicks);
                _logger.LogInformation("\"{Title}\" S{Season}: fanning out to {Count} episode(s)", job.Title, target.Season, episodePicks.Count);
                if (gaps.Count > 0)
                    _logger.LogWarning("\"{Title}\" S{Season}: no release for episode(s) {Gaps} — request may complete partially",
                        job.Title, target.Season, string.Join(",", gaps));
            }
        }

        if (items.Count == 0) return DownloadPlan.None;
        var kind = anyEpisodes ? DownloadPlanKind.Episodes : DownloadPlanKind.SeasonPack;
        return new DownloadPlan(kind, items);
    }

    private DownloadPlan PlanEpisodes(List<Annotated> acceptable, IEnumerable<(int Season, int Episode)> wanted, FulfillmentJobDto job)
    {
        var items = new List<DownloadPlanItem>();
        foreach (var (season, episode) in wanted)
        {
            var best = acceptable.Where(a => !a.IsPack && a.Season == season && a.Episode == episode)
                                 .OrderByDescending(a => a.Score).FirstOrDefault();
            if (best is not null) items.Add(ToItem(best) with { Season = season, Episode = episode });
            else _logger.LogWarning("\"{Title}\": no acceptable release for S{Season}E{Episode}", job.Title, season, episode);
        }
        return items.Count == 0 ? DownloadPlan.None : new DownloadPlan(DownloadPlanKind.Episodes, items);
    }

    /// <summary>Prefer the precise SeasonTargets from enqueue; else derive pack-only targets from RequestedSeasons.</summary>
    private static List<SeasonTarget> ResolveSeasonTargets(FulfillmentJobDto job)
    {
        if (job.SeasonTargets.Count > 0) return job.SeasonTargets;
        return job.RequestedSeasons.Select(s => new SeasonTarget { Season = s, EpisodeCount = 0, MissingEpisodes = new() }).ToList();
    }

    // ---- Scoring / filtering -----------------------------------------------------------------------

    private Annotated Annotate(ReleaseCandidate c, FulfillmentJobDto job, EffectiveDownloadPreferences p)
    {
        var parsed = _parser.Parse(c.ReleaseName);
        int res = EffectiveResolution(c, parsed);
        int floor = (int)job.Quality;

        int? season = c.Season ?? parsed.Season;
        int? episode = c.Episode ?? parsed.Episode;
        bool isPack = episode is null && (parsed.IsSeasonPack || season is not null);

        double maxSize = isPack ? p.MaxSeasonPackSizeGb : p.MaxSizeGb;
        bool acceptable =
            (!p.EnforceQualityFloor || floor == 0 || res >= floor) &&
            c.Seeders >= p.MinSeeders &&
            c.SizeGb >= 0.05 &&               // reject 0-byte / fake entries
            c.SizeGb <= maxSize;

        double score = Score(c, parsed, res, floor, isPack, p);
        return new Annotated(c, season, episode, isPack, score, acceptable);
    }

    private int EffectiveResolution(ReleaseCandidate c, ParsedRelease parsed)
    {
        var fromLabel = _parser.ResolutionFromLabel(c.QualityLabel);
        return fromLabel > 0 ? fromLabel : parsed.Resolution;
    }

    private static double Score(ReleaseCandidate c, ParsedRelease p, int res, int floor, bool isPack, EffectiveDownloadPreferences prefs)
    {
        double s = 0;
        if (floor > 0) s += res >= floor ? 500 : (prefs.EnforceQualityFloor ? -1000 : -200);
        s += Math.Min(res, 2160) / 10.0;                              // mild preference for higher resolution
        if (prefs.PreferHigherQualitySource) s += (int)p.Source * 60;  // BluRay/Remux > WebDl > WebRip > HDTV
        s += Math.Log10(Math.Max(1, c.Seeders)) * 80;                 // seeders, diminishing returns
        if (p.ProperOrRepack) s += 20;
        if (prefs.PreferX265 && p.Codec == "x265") s += 15;            // efficient size for same quality
        if (prefs.PreferHdr && p.Hdr) s += 10;
        if (p.Group is not null && prefs.PreferredGroups.Contains(p.Group, StringComparer.OrdinalIgnoreCase)) s += 100;
        if (isPack) s += 25;                                           // nudge a pack ahead of scattered episodes
        return s;
    }

    private static DownloadPlanItem ToItem(Annotated a) => new(a.C, a.Season, a.Episode, a.IsPack);

    private void Log(string kind, Annotated a, FulfillmentJobDto job) =>
        _logger.LogInformation("Selected [{Kind}] \"{Name}\" ({Seeders} seeders, {SizeGb:F1}GB, score {Score:F0}) for \"{Title}\"",
            kind, a.C.ReleaseName, a.C.Seeders, a.C.SizeGb, a.Score, job.Title);
}
