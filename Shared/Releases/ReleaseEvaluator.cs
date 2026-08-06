using System.Text.RegularExpressions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared.Releases;

public interface IReleaseEvaluator
{
    /// <summary>Judge one release against a job and its ranking context.</summary>
    RankedCandidate Evaluate(ReleaseCandidate candidate, FulfillmentJobDto job, RankingContext context);

    /// <summary>Judge a batch, ordered best-first among the accepted ones.</summary>
    IReadOnlyList<RankedCandidate> EvaluateAll(IEnumerable<ReleaseCandidate> candidates, FulfillmentJobDto job, RankingContext context);
}

/// <summary>
/// Decides whether a release is acceptable for a job and how good it is, returning both as data.
///
/// The quality decision is now made against the request's PROFILE — the ordered list of (resolution ×
/// source) tiers the admin marked allowed — rather than a single pixel-height floor. That's the difference
/// between "1080p or better" and "prefer WEBDL-1080p, accept Bluray-1080p as equivalent, take 720p only
/// rather than nothing, and never take a CAM". The flat floor is still honoured when a job has no profile.
///
/// Pure: no I/O, no logging, no clock of its own. That keeps the Shared assembly free of package
/// references and lets the same evaluation run in the downloader and in the web app.
/// </summary>
public class ReleaseEvaluator(IReleaseParser parser) : IReleaseEvaluator
{
    private readonly IReleaseParser _parser = parser;

    public IReadOnlyList<RankedCandidate> EvaluateAll(IEnumerable<ReleaseCandidate> candidates, FulfillmentJobDto job, RankingContext context) =>
        candidates.Select(c => Evaluate(c, job, context))
                  .OrderByDescending(r => r.Accepted)
                  .ThenByDescending(r => r.Score)
                  .ToList();

    public RankedCandidate Evaluate(ReleaseCandidate c, FulfillmentJobDto job, RankingContext context)
    {
        var p = context.Preferences;
        var parsed = _parser.Parse(c.ReleaseName);
        var resolution = EffectiveResolution(c, parsed);
        var rejections = new List<Rejection>();

        // ---- Quality: tier first, flat floor as the fallback -----------------------------------------
        var definition = ResolveTier(resolution, parsed.Source, context.Definitions);
        var (rank, allowed) = RankInProfile(definition, context.Profile);
        int floor = (int)job.Quality;

        if (context.Profile is not null)
        {
            if (definition is null)
                rejections.Add(new Rejection(RejectionReason.NotInProfile,
                    $"no quality tier matches {(resolution > 0 ? resolution + "p" : "an unknown resolution")} from {parsed.Source}"));
            else if (!allowed && !context.RelaxQualityFloor)
                rejections.Add(new Rejection(RejectionReason.NotInProfile,
                    $"{definition.Name} isn't allowed by the \"{context.Profile.Name}\" profile"));
            else if (!allowed)
                // Relaxed: a disallowed tier is still preferable to nothing, but it must not be a CAM —
                // those are never what anyone meant, at any point.
                if (parsed.Source == ReleaseSource.Cam)
                    rejections.Add(new Rejection(RejectionReason.NotInProfile, "CAM releases are never acceptable"));
        }
        else if (p.EnforceQualityFloor && !context.RelaxQualityFloor && floor > 0 && resolution < floor)
        {
            rejections.Add(new Rejection(RejectionReason.BelowQualityFloor,
                $"{(resolution > 0 ? resolution + "p" : "unknown resolution")} is below the {floor}p target"));
        }

        // An upgrade must actually be an upgrade. Enforced regardless of relaxation: replacing a file with
        // something no better is pure churn.
        if (job.IsUpgrade && floor > 0 && resolution < floor)
            rejections.Add(new Rejection(RejectionReason.NotAnUpgrade,
                $"{resolution}p is not better than the {floor}p already in the library"));

        // ---- Season / episode ------------------------------------------------------------------------
        int? season = c.Season ?? parsed.Season;
        int? episode = c.Episode ?? parsed.Episode;
        bool isPack = episode is null && (parsed.IsSeasonPack || season is not null);

        // ---- Seeders, size ---------------------------------------------------------------------------
        int minSeeders = context.Profile?.MinSeeders ?? p.MinSeeders;
        if (c.SeedersKnown && c.Seeders < minSeeders)
            rejections.Add(new Rejection(RejectionReason.TooFewSeeders, $"{c.Seeders} seeders, minimum is {minSeeders}"));

        double maxSize = isPack
            ? context.Profile?.MaxSeasonPackSizeGb ?? p.MaxSeasonPackSizeGb
            : context.Profile?.MaxSizeGb ?? p.MaxSizeGb;

        if (c.SizeKnown)
        {
            if (c.SizeGb < 0.05)
                rejections.Add(new Rejection(RejectionReason.SizeTooSmall, $"{c.SizeGb:F2} GB looks like a fake or empty torrent"));
            else if (c.SizeGb > maxSize)
                rejections.Add(new Rejection(RejectionReason.SizeTooLarge, $"{c.SizeGb:F1} GB exceeds the {maxSize:F0} GB limit"));
        }
        // Size unknown is NOT a rejection. The HTML scrapers frequently fail to parse it, and treating that
        // as "0 bytes" silently discarded a large share of their results against the minimum-size gate.

        // ---- Identity --------------------------------------------------------------------------------
        var (idMatch, idMismatch) = CompareImdb(job.ImdbId, c.ImdbId);
        double titleRecall = TitleSimilarity(parsed.Title, job.Title);
        int extraTokens = ExtraTitleTokens(parsed.Title, job.Title);
        int jobRawTokens = RawTokenCount(job.Title);
        // Tolerance for extra words scales with how specific the request is: a one-word title like "Lucky"
        // tolerates none (so it rejects "Lucky Star"), while "The Office" tolerates one (so it accepts the
        // regional variant "The Office US").
        int maxExtra = jobRawTokens <= 1 ? 0 : jobRawTokens <= 3 ? 1 : 2;

        if (idMismatch)
            rejections.Add(new Rejection(RejectionReason.ImdbMismatch, $"IMDb {c.ImdbId} is a different title to {job.ImdbId}"));
        else if (!idMatch)
        {
            // The id is far stronger than fuzzy text, so the title gate only applies when there's no id.
            if (titleRecall < p.MinTitleSimilarity)
                rejections.Add(new Rejection(RejectionReason.TitleMismatch,
                    $"\"{parsed.Title}\" only matches {titleRecall:P0} of \"{job.Title}\" (minimum {p.MinTitleSimilarity:P0})"));
            else if (extraTokens > maxExtra)
                rejections.Add(new Rejection(RejectionReason.ExtraTitleTokens,
                    $"\"{parsed.Title}\" has {extraTokens} extra word(s) — it looks like a different title"));
        }

        if (job.Year is int jy && parsed.Year is int py && Math.Abs(jy - py) > 1)
            rejections.Add(new Rejection(RejectionReason.YearMismatch, $"released {py}, expected {jy}"));

        if (job.MediaType == MediaType.Movie && (episode is not null || isPack))
            rejections.Add(new Rejection(RejectionReason.MediaTypeMismatch, "this is a TV release but the request is a movie"));

        // ---- Blocklist / age -------------------------------------------------------------------------
        var hash = MagnetUtil.Normalize(c.InfoHash) ?? MagnetUtil.InfoHashFromMagnet(c.Magnet);
        if (hash is not null && context.BlocklistedHashes.Contains(hash))
            rejections.Add(new Rejection(RejectionReason.Blocklisted, "this release already failed for this request"));

        if (string.IsNullOrWhiteSpace(c.Magnet))
            rejections.Add(new Rejection(RejectionReason.NoMagnet, "no magnet link"));

        // Custom formats: a user-defined score on top of the structural one, and a floor the profile can
        // set so "never take anything scoring below X" is expressible.
        var (formatScore, matchedFormats) = context.CustomFormats.Count == 0
            ? (0, new List<string>())
            : CustomFormatMatcher.Score(parsed, c, context.CustomFormats, context.CustomFormatScores);

        if (context.Profile is { MinCustomFormatScore: var minScore } && minScore != 0 && formatScore < minScore)
            rejections.Add(new Rejection(RejectionReason.CustomFormatScoreTooLow,
                $"custom-format score {formatScore} is below the profile's minimum of {minScore}"));

        if (context.Profile?.AllowedLanguagesCsv is { Length: > 0 } allowedCsv && parsed.Languages.Count > 0)
        {
            var allowedLanguages = allowedCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (allowedLanguages.Length > 0 && !parsed.Languages.Any(l => allowedLanguages.Contains(l, StringComparer.OrdinalIgnoreCase)))
                rejections.Add(new Rejection(RejectionReason.LanguageNotAllowed,
                    $"languages [{string.Join(", ", parsed.Languages)}] aren't in the profile's allowed set"));
        }

        var score = Score(c, parsed, resolution, rank, isPack, context, idMatch, titleRecall, formatScore);

        return new RankedCandidate
        {
            Candidate = c,
            Parsed = parsed,
            Resolution = resolution,
            QualityDefinitionId = definition?.Id,
            ProfileRank = rank,
            Season = season,
            SeasonEnd = parsed.SeasonEnd,
            Episode = episode,
            EpisodeStart = parsed.EpisodeStart,
            EpisodeEnd = parsed.EpisodeEnd,
            IsPack = isPack,
            LooksLikeCompleteSeries = parsed.LooksLikeCompleteSeries,
            Accepted = rejections.Count == 0,
            Rejections = rejections,
            Score = score.Total,
            ScoreBreakdown = score.Components,
            CustomFormatScore = formatScore,
            MatchedFormats = matchedFormats
        };
    }

    // ---- Quality tiers ------------------------------------------------------------------------------

    /// <summary>
    /// The catalog row for a release's (resolution, source). Falls back to the Unknown-source row for that
    /// resolution — a large share of real release names carry no recognisable source token, and without
    /// that fallback those releases would resolve to no tier and be unrankable.
    /// </summary>
    internal static QualityDefinitionDto? ResolveTier(int resolution, ReleaseSource source, IReadOnlyList<QualityDefinitionDto> definitions)
    {
        if (definitions.Count == 0 || resolution <= 0) return null;
        var tier = (int)QualityHelper.FromHeight(resolution);
        return definitions.FirstOrDefault(d => d.Resolution == tier && d.Source == source)
            ?? definitions.FirstOrDefault(d => d.Resolution == tier && d.Source == ReleaseSource.Unknown);
    }

    /// <summary>Where a tier sits in a profile: its rank (higher is better) and whether it's allowed.</summary>
    internal static (int? Rank, bool Allowed) RankInProfile(QualityDefinitionDto? definition, QualityProfileDto? profile)
    {
        if (definition is null || profile is null) return (null, true);
        for (int i = 0; i < profile.Items.Count; i++)
        {
            var item = profile.Items[i];
            bool hit = item.Members is { Length: > 0 } m
                ? m.Contains(definition.Id)
                : item.K == $"q:{definition.Id}";
            if (hit) return (i, item.Allowed);
        }
        return (null, false);
    }

    /// <summary>True when the library already holds something at or above the profile's cutoff.</summary>
    public static bool MeetsCutoff(int? achievedDefinitionId, QualityProfileDto profile, IReadOnlyList<QualityDefinitionDto> definitions)
    {
        if (achievedDefinitionId is null) return false;
        var have = definitions.FirstOrDefault(d => d.Id == achievedDefinitionId.Value);
        var want = definitions.FirstOrDefault(d => d.Id == profile.CutoffQualityDefinitionId);
        return have is not null && want is not null && have.SortWeight >= want.SortWeight;
    }

    // ---- Scoring ------------------------------------------------------------------------------------

    private static (double Total, List<ScoreComponent> Components) Score(
        ReleaseCandidate c, ParsedRelease p, int resolution, int? profileRank, bool isPack,
        RankingContext context, bool idMatch, double titleRecall, int formatScore)
    {
        var prefs = context.Preferences;
        var parts = new List<ScoreComponent>();
        void Add(string name, double points) { if (points != 0) parts.Add(new ScoreComponent(name, points)); }

        // Position in the profile dominates: a tier the admin ranked higher should win, full stop.
        if (profileRank is int r) Add("Profile rank", r * 100);
        else Add("Resolution", Math.Min(resolution, 2160) / 10.0);

        if (prefs.PreferHigherQualitySource) Add("Source", (int)p.Source * 20);
        // Seeders matter but with heavy diminishing returns — 2000 seeders isn't twice as good as 1000.
        if (c.SeedersKnown) Add("Seeders", Math.Log10(Math.Max(1, c.Seeders)) * 80);
        else Add("Seeders unknown", -25);

        if (p.ProperOrRepack) Add("PROPER/REPACK", 20);
        if (prefs.PreferX265 && p.Codec == "x265") Add("x265", 15);
        if (prefs.PreferHdr && p.Hdr) Add("HDR", 10);
        if (p.Group is not null && prefs.PreferredGroupsCsv is { Length: > 0 } groups
            && groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Contains(p.Group, StringComparer.OrdinalIgnoreCase))
            Add("Preferred group", 100);
        if (isPack) Add("Season pack", 25);

        Add(idMatch ? "IMDb id match" : "Title match", idMatch ? 200 : titleRecall * 40);
        // Deliberately unscaled: an admin who sets -10000 on CAM expects that to be decisive.
        Add("Custom formats", formatScore);

        // Indexer priority only ever breaks near-ties; deliberately smaller than any quality signal.
        if (context.IndexerPriorities.TryGetValue(c.IndexerId, out var priority))
            Add("Indexer priority", Math.Clamp(50 - priority, 0, 49));

        return (parts.Sum(x => x.Points), parts);
    }

    private int EffectiveResolution(ReleaseCandidate c, ParsedRelease parsed)
    {
        var fromLabel = _parser.ResolutionFromLabel(c.QualityLabel);
        return fromLabel > 0 ? fromLabel : parsed.Resolution;
    }

    // ---- Title matching -----------------------------------------------------------------------------

    private static readonly HashSet<string> StopTokens = new(StringComparer.OrdinalIgnoreCase)
        { "the", "a", "an", "and", "of", "1080p", "2160p", "720p", "480p", "x264", "x265", "h264", "h265",
          "web", "webdl", "webrip", "bluray", "hdtv", "repack", "proper", "hdr", "dv" };

    /// <summary>Region/variant tags releasers append to disambiguate a country's edition of the same show
    /// ("Bluey AU", "The Office US"). Not evidence of a different, longer title.</summary>
    private static readonly HashSet<string> RegionTokens = new(StringComparer.OrdinalIgnoreCase)
        { "us", "uk", "au", "nz", "ca", "gb" };

    /// <summary>
    /// Recall of the requested title's significant words within the release's parsed core title. Weighted
    /// toward the request rather than union-based, because release names legitimately carry extra
    /// quality/group tokens the title doesn't.
    /// </summary>
    internal static double TitleSimilarity(string releaseTitle, string jobTitle)
    {
        var a = Tokenize(releaseTitle);
        var b = Tokenize(jobTitle);
        if (a.Count == 0 || b.Count == 0) return 0;
        return (double)a.Intersect(b).Count() / b.Count;
    }

    private static HashSet<string> Tokenize(string s) =>
        Regex.Matches(s.ToLowerInvariant(), @"[a-z0-9]+")
            .Select(m => m.Value)
            .Where(t => t.Length > 1 && !StopTokens.Contains(t))
            .ToHashSet();

    private static int RawTokenCount(string s) =>
        Regex.Matches(s.ToLowerInvariant(), @"[a-z0-9]+").Count(m => m.Value.Length > 1);

    /// <summary>Significant words in the release's core title the request doesn't have — what separates
    /// "Lucky Star" from "Lucky".</summary>
    internal static int ExtraTitleTokens(string releaseTitle, string jobTitle)
    {
        var rel = Tokenize(releaseTitle);
        var job = Tokenize(jobTitle);
        if (rel.Count == 0) return 0; // couldn't parse a core title — the other gates still apply
        return rel.Except(job).Count(t => !RegionTokens.Contains(t));
    }

    internal static (bool match, bool mismatch) CompareImdb(string? jobImdb, string? candidateImdb)
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
}
