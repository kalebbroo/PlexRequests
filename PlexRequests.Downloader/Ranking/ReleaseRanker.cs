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

    private sealed record Annotated(ReleaseCandidate C, int? Season, int? SeasonEnd, int? Episode, bool IsPack, bool LooksLikeCompleteSeries, double Score, bool Acceptable, int Resolution);

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
            // Summarize the candidates' resolutions so the "why nothing?" answer is visible without debug logs
            // (e.g. "all candidates were 720p but the floor is 1080p" — the classic cause of a deferred request).
            var resolutions = annotated.Select(a => a.Resolution).Where(r => r > 0).OrderByDescending(r => r).ToList();
            var resSummary = resolutions.Count > 0 ? string.Join("/", resolutions.Distinct().Select(r => $"{r}p")) : "none parsed";
            _logger.LogInformation("No acceptable release for \"{Title}\"{Upgrade} (of {Total} candidate(s); target floor={Floor}p, minSeeders={Min}; candidate resolutions: {Res})",
                job.Title, job.IsUpgrade ? " [upgrade]" : "", candidates.Count, floor, p.MinSeeders, resSummary);
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

        // Authoritative identity check first: if the provider gave us an IMDb id for this release AND the
        // job has one, they must agree. A match trusts the release regardless of title text (the id is far
        // stronger than fuzzy name matching); a mismatch is a hard reject (definitively the wrong title).
        var (idMatch, idMismatch) = CompareImdb(job.ImdbId, c.ImdbId);

        // Strict, bidirectional title check for the id-less case (free-text indexers). The old check was
        // recall-only — "do the request's words appear in the release name" — which scored a one-word
        // request like "Lucky" as a perfect match for "Lucky Star" (the extra word "star" was free). Now we
        // compare against the release's parsed core TITLE (tags stripped) and also reject when that title
        // carries significant words the request doesn't have, scaled by how specific the request is.
        double titleRecall = TitleSimilarity(parsed.Title, job.Title);
        int extraTokens = ExtraTitleTokens(parsed.Title, job.Title);
        // Tolerance for extra words in the release's core title scales with how specific the request is,
        // measured by RAW word count (stop-words included) — so a 1-word title like "Lucky" tolerates 0
        // extra words (rejects "Lucky Star"), while "The Office" (2 raw words) tolerates 1 (accepts the
        // regional variant "The Office US"). Prevents the short-title false positive without over-rejecting.
        int jobRawTokens = RawTokenCount(job.Title);
        int maxExtra = jobRawTokens <= 1 ? 0 : jobRawTokens <= 3 ? 1 : 2;
        bool titleOk = titleRecall >= p.MinTitleSimilarity && extraTokens <= maxExtra;

        // Year mismatch penalty only applies when both sides actually have a year to compare — a release
        // with no parsed year, or a job with no resolved year, gets no penalty either way.
        bool yearMismatch = job.Year is int jy && parsed.Year is int py && Math.Abs(jy - py) > 1;
        // A movie job can never legitimately match a release that parses as a TV episode/season pack.
        bool mediaTypeMismatch = job.MediaType == MediaType.Movie && (episode is not null || isPack);

        bool identityOk = idMatch || (!idMismatch && titleOk); // id wins; else fall back to the strict title gate

        // An upgrade job enforces the quality floor unconditionally: replacing a file only ever makes sense
        // with something at or above the preferred quality, never a downgrade/side-grade — even when the
        // global EnforceQualityFloor is off for first-time grabs.
        bool enforceFloor = p.EnforceQualityFloor || job.IsUpgrade;

        bool acceptable =
            (!enforceFloor || floor == 0 || res >= floor) &&
            c.Seeders >= p.MinSeeders &&
            c.SizeGb >= 0.05 &&               // reject 0-byte / fake entries
            c.SizeGb <= maxSize &&
            identityOk &&
            !yearMismatch &&
            !mediaTypeMismatch;

        if (!acceptable)
            _logger.LogDebug("Rejected \"{Name}\" for \"{Title}\": res={Res} floor={Floor} enforceFloor={Enforce}, seeders={Seeders}/{MinSeeders}, sizeGb={Size:F1}/{MaxSize:F0}, idMismatch={IdMismatch}, titleRecall={Recall:F2}, extraTokens={Extra}, yearMismatch={YearMismatch}, mediaTypeMismatch={MediaMismatch} (coreTitle=\"{Core}\")",
                c.ReleaseName, job.Title, res, floor, enforceFloor, c.Seeders, p.MinSeeders, c.SizeGb, maxSize, idMismatch, titleRecall, extraTokens, yearMismatch, mediaTypeMismatch, parsed.Title);

        // A confirmed id match is worth a big boost; otherwise reward title recall as before.
        double score = Score(c, parsed, res, floor, isPack, p, enforceFloor) + (idMatch ? 200 : titleRecall * 40);
        return new Annotated(c, season, parsed.SeasonEnd, episode, isPack, parsed.LooksLikeCompleteSeries, score, acceptable, res);
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

    // Raw word count (length > 1), stop-words INCLUDED — a specificity measure for the request title.
    private static int RawTokenCount(string s) =>
        System.Text.RegularExpressions.Regex.Matches(s.ToLowerInvariant(), @"[a-z0-9]+").Count(m => m.Value.Length > 1);

    // Significant words in the release's core title that the requested title does NOT contain. This is what
    // separates "Lucky Star" (extra: "star") from "Lucky" — a strong signal it's a different, longer title.
    private static int ExtraTitleTokens(string releaseTitle, string jobTitle)
    {
        var rel = Tokenize(releaseTitle);
        var job = Tokenize(jobTitle);
        if (rel.Count == 0) return 0; // couldn't parse a core title — don't penalize (other gates still apply)
        return rel.Except(job).Count();
    }

    // Compare an optional job IMDb id with an optional candidate IMDb id. Returns (match, mismatch): both
    // false when either side is missing (no signal). Ids are normalized to their numeric core (tt0944947,
    // "944947", 944947 all compare equal).
    private static (bool match, bool mismatch) CompareImdb(string? jobImdb, string? candidateImdb)
    {
        var a = NormalizeImdb(jobImdb);
        var b = NormalizeImdb(candidateImdb);
        if (a is null || b is null) return (false, false);
        return a == b ? (true, false) : (false, true);
    }

    private static string? NormalizeImdb(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var digits = new string(id.Where(char.IsDigit).ToArray()).TrimStart('0');
        return digits.Length == 0 ? null : digits;
    }

    private int EffectiveResolution(ReleaseCandidate c, ParsedRelease parsed)
    {
        var fromLabel = _parser.ResolutionFromLabel(c.QualityLabel);
        return fromLabel > 0 ? fromLabel : parsed.Resolution;
    }

    private static double Score(ReleaseCandidate c, ParsedRelease p, int res, int floor, bool isPack, EffectiveDownloadPreferences prefs, bool enforceFloor)
    {
        double s = 0;
        if (floor > 0) s += res >= floor ? 500 : (enforceFloor ? -1000 : -200);
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

    private static DownloadPlanItem ToItem(Annotated a) => new(a.C, a.Season, a.Episode, a.IsPack) { Resolution = a.Resolution };

    private void Log(string kind, Annotated a, FulfillmentJobDto job) =>
        _logger.LogInformation("Selected [{Kind}]{Upgrade} \"{Name}\" ({Res}p, {Seeders} seeders, {SizeGb:F1}GB, score {Score:F0}) for \"{Title}\" (target {Floor}p)",
            kind, job.IsUpgrade ? " UPGRADE" : "", a.C.ReleaseName, a.Resolution, a.C.Seeders, a.C.SizeGb, a.Score, job.Title, (int)job.Quality);
}
