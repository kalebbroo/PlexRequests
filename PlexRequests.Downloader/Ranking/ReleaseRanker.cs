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

    private sealed record Annotated(ReleaseCandidate C, int? Season, int? SeasonEnd, int? Episode, bool IsPack, bool LooksLikeCompleteSeries, double Score, bool Acceptable);

    // A pack matches a requested season if: it names that exact season, the season falls within its
    // parsed multi-season range (S01-S05), or it's an explicitly-labeled "complete series" pack. A release
    // that simply failed to parse any season is NOT treated as matching every season (see LooksLikeCompleteSeries).
    private static bool MatchesSeason(Annotated a, int targetSeason)
    {
        if (a.Season is int s)
            return a.SeasonEnd is int end ? targetSeason >= s && targetSeason <= end : s == targetSeason;
        return a.LooksLikeCompleteSeries;
    }

    // What to keep from a pack chosen for a season target: the specific missing episodes when we know them
    // (so the download client skips the rest and we don't re-import what Plex already has), else null =
    // "take the whole pack" (we don't know what's missing — e.g. a metadata miss).
    private static IReadOnlyList<int>? PackNeeded(SeasonTarget target) =>
        target.MissingEpisodes.Count > 0 ? target.MissingEpisodes : null;

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
            var packs = acceptable.Where(a => a.IsPack && MatchesSeason(a, target.Season))
                                  .OrderByDescending(a => a.Score).ToList();
            var bestPack = packs.FirstOrDefault();

            bool wantPackFirst = p.SeasonPackStrategy is SeasonPackStrategy.PreferPack or SeasonPackStrategy.Auto;

            if (wantPackFirst && bestPack is not null)
            {
                Log("season-pack", bestPack, job);
                items.Add(ToItem(bestPack) with { Season = target.Season, NeededEpisodes = PackNeeded(target) });
                continue;
            }

            // Episode fallback (or PreferEpisodes): assemble the best release per missing episode.
            if (!p.AllowEpisodeFallback && p.SeasonPackStrategy != SeasonPackStrategy.PreferEpisodes)
            {
                if (bestPack is not null) { items.Add(ToItem(bestPack) with { Season = target.Season, NeededEpisodes = PackNeeded(target) }); continue; }
                _logger.LogInformation("No acceptable pack for \"{Title}\" S{Season} and episode fallback disabled", job.Title, target.Season);
                continue;
            }

            var missing = target.MissingEpisodes;
            if (missing.Count == 0)
            {
                // No episode list to fan out to (metadata miss) → pack is the only option (take it whole).
                if (bestPack is not null) { items.Add(ToItem(bestPack) with { Season = target.Season }); }
                else _logger.LogInformation("No pack and no episode list for \"{Title}\" S{Season}; skipping", job.Title, target.Season);
                continue;
            }
            if (missing.Count > p.MaxEpisodesForFanout)
            {
                if (bestPack is not null) { items.Add(ToItem(bestPack) with { Season = target.Season, NeededEpisodes = PackNeeded(target) }); continue; }
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

            // If some episodes have no standalone release, use the pack to cover the missing episodes
            // instead (trimmed to just what's missing — we don't re-fetch what Plex already has).
            if (gaps.Count > 0 && bestPack is not null)
            {
                _logger.LogInformation("\"{Title}\" S{Season}: {Gaps} episode(s) had no standalone release → using season pack (trimmed to missing episodes)",
                    job.Title, target.Season, gaps.Count);
                items.Add(ToItem(bestPack) with { Season = target.Season, NeededEpisodes = PackNeeded(target) });
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

    // Explicit episode targets. Prefer a standalone release per episode; but when an episode has no
    // standalone release (common for shows only ever packaged as season packs — kids'/preschool titles
    // especially), fall back to the season pack, trimmed to just the requested episodes so we don't pull
    // or import the rest of the season. Episodes are grouped by season so one pack covers a season's gaps.
    private DownloadPlan PlanEpisodes(List<Annotated> acceptable, IEnumerable<(int Season, int Episode)> wanted, FulfillmentJobDto job)
    {
        var p = _prefs.Current;
        var items = new List<DownloadPlanItem>();

        foreach (var seasonGroup in wanted.GroupBy(w => w.Season))
        {
            int season = seasonGroup.Key;
            var episodes = seasonGroup.Select(w => w.Episode).Distinct().OrderBy(e => e).ToList();
            var bestPack = acceptable.Where(a => a.IsPack && MatchesSeason(a, season))
                                     .OrderByDescending(a => a.Score).FirstOrDefault();

            // Too many episodes to fan out one-by-one and a pack is available → take the pack, trimmed to
            // exactly the requested episodes.
            if (episodes.Count > p.MaxEpisodesForFanout && bestPack is not null)
            {
                Log("season-pack (episode request over fan-out cap)", bestPack, job);
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
                _logger.LogInformation("\"{Title}\" S{Season}: {Count} requested episode(s) have no standalone release → using season pack trimmed to episode(s) {Gaps}",
                    job.Title, season, gaps.Count, string.Join(",", gaps));
                items.Add(ToItem(bestPack) with { Season = season, NeededEpisodes = gaps });
            }
            else
            {
                _logger.LogWarning("\"{Title}\" S{Season}: no standalone release and no season pack for episode(s) {Gaps}",
                    job.Title, season, string.Join(",", gaps));
            }
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
        double titleSim = TitleSimilarity(c.ReleaseName, job.Title);
        // Year mismatch penalty only applies when both sides actually have a year to compare — a release
        // with no parsed year, or a job with no resolved year, gets no penalty either way.
        bool yearMismatch = job.Year is int jy && parsed.Year is int py && Math.Abs(jy - py) > 1;
        // A movie job can never legitimately match a release that parses as a TV episode/season pack —
        // this is a much stronger signal than title text similarity, which text alone can't reliably
        // catch when the requested title is a short/common word that's also a substring of an unrelated
        // show's title (e.g. job "Protector" matching a release for "Protector of Kanae" S01E12: pure
        // token-overlap scores that as a perfect match on the job title's single token, but the release
        // unambiguously parses as a TV episode, which no movie release ever does).
        bool mediaTypeMismatch = job.MediaType == MediaType.Movie && (episode is not null || isPack);

        bool acceptable =
            (!p.EnforceQualityFloor || floor == 0 || res >= floor) &&
            c.Seeders >= p.MinSeeders &&
            c.SizeGb >= 0.05 &&               // reject 0-byte / fake entries
            c.SizeGb <= maxSize &&
            titleSim >= p.MinTitleSimilarity &&
            !yearMismatch &&
            !mediaTypeMismatch;

        double score = Score(c, parsed, res, floor, isPack, p) + titleSim * 40;
        return new Annotated(c, season, parsed.SeasonEnd, episode, isPack, parsed.LooksLikeCompleteSeries, score, acceptable);
    }

    private static readonly HashSet<string> StopTokens = new(StringComparer.OrdinalIgnoreCase)
        { "the", "a", "an", "and", "of", "1080p", "2160p", "720p", "480p", "x264", "x265", "h264", "h265",
          "web", "webdl", "webrip", "bluray", "hdtv", "repack", "proper", "hdr", "dv" };

    // Cheap, dependency-free normalized token-set overlap (Jaccard) between a release name and the
    // requested title — the acceptability gate that stops free-text-search indexers (1337x/ext.to/Nyaa)
    // from accepting an unrelated/wrong-show/spam release just because it clears quality/seeder/size.
    private static double TitleSimilarity(string releaseName, string jobTitle)
    {
        var a = Tokenize(releaseName);
        var b = Tokenize(jobTitle);
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersect = a.Intersect(b).Count();
        // Recall-weighted toward the job title: every significant word of the requested title should
        // show up somewhere in the release name (release names commonly have extra quality/group tokens
        // the title doesn't, so union-based Jaccard would unfairly punish that).
        return (double)intersect / b.Count;
    }

    private static HashSet<string> Tokenize(string s) =>
        System.Text.RegularExpressions.Regex.Matches(s.ToLowerInvariant(), @"[a-z0-9]+")
            .Select(m => m.Value)
            .Where(t => t.Length > 1 && !StopTokens.Contains(t))
            .ToHashSet();

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
