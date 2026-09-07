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
        if (job.MediaType == MediaType.Music)
            return MusicReleaseEvaluator.Evaluate(c, job, context, parsed);
        var isAnime = job.IsAnime || job.MediaType == MediaType.Anime;
        var languagePreference = job.MediaLanguagePolicy?.Preference ?? context.Profile?.LanguagePreference;
        var preferredAudio = MediaLanguagePolicy.Normalize(job.MediaLanguagePolicy?.PreferredAudioLanguage
                                                            ?? context.Profile?.PreferredAudioLanguage) ?? "en";
        var resolution = EffectiveResolution(c, parsed);
        var rejections = new List<Rejection>();

        // ---- Quality: tier first, flat floor as the fallback -----------------------------------------
        var definition = ResolveTier(resolution, parsed.Source, context.Definitions);
        var (rank, allowed) = RankInProfile(definition, context.Profile);
        int floor = (int)job.Quality;

        if (context.Profile is not null)
        {
            // The cutoff is a ceiling, not just the point where auto-upgrade searches stop. Every seeded
            // profile marks every tier from its floor up through 8K "allowed" — that list only ever existed
            // to gate the FLOOR, so a request for 1080p would happily grab a 2160p/4320p release because it
            // scored higher and nothing said no. A user who picks "1080p" means exactly that, not "1080p or
            // better" — bigger files, more bandwidth, and playback devices that may not handle 4K weren't
            // asked for. Rank the cutoff the same way a candidate's own tier is ranked and reject anything
            // strictly above it — except while relaxed, where nothing AT the target was found after repeated
            // empty searches and something is better than nothing.
            var cutoffDefinition = context.Definitions.FirstOrDefault(d => d.Id == context.Profile.CutoffQualityDefinitionId);
            var (cutoffRank, _) = RankInProfile(cutoffDefinition, context.Profile);
            bool aboveCutoff = rank is int rk && cutoffRank is int cr && rk > cr;

            if (definition is null)
                rejections.Add(new Rejection(RejectionReason.NotInProfile,
                    $"no quality tier matches {(resolution > 0 ? resolution + "p" : "an unknown resolution")} from {parsed.Source}"));
            else if (aboveCutoff && !context.RelaxQualityFloor)
                rejections.Add(new Rejection(RejectionReason.AboveCutoff,
                    $"{definition.Name} is above the \"{context.Profile.Name}\" profile's {cutoffDefinition?.Name ?? "cutoff"} target"));
            else if (!allowed && !context.RelaxQualityFloor)
                rejections.Add(new Rejection(RejectionReason.NotInProfile,
                    $"{definition.Name} isn't allowed by the \"{context.Profile.Name}\" profile"));
            else if (!allowed || aboveCutoff)
                // Relaxed: a disallowed or above-cutoff tier is still preferable to nothing, but it must not
                // be a CAM — those are never what anyone meant, at any point.
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
        int? sourceSeason = c.Season ?? parsed.Season;
        int? sourceEpisode = c.Episode ?? parsed.Episode;
        if (parsed.FractionalEpisodeNumber)
            rejections.Add(new Rejection(RejectionReason.EpisodeMappingMissing,
                "fractional/special episode notation requires an explicit canonical mapping; it cannot be truncated to an integer episode"));
        var seasonIdentity = isAnime && !EpisodeOrderMapping.IsActive(job.EpisodeOrderProfile)
            ? job.CanonicalSeasons.Count > 0
                ? AnimeSeasonIdentity.Match(c.ReleaseName, job.Title, job.CanonicalSeasons, sourceSeason)
                : AnimeSeasonIdentity.Match(c.ReleaseName, job.Title, job.SeasonTargets, sourceSeason)
            : null;
        var singleFileLimit = context.Profile?.MaxSizeGb ?? p.MaxSizeGb;
        var packLimit = context.Profile?.MaxSeasonPackSizeGb ?? p.MaxSeasonPackSizeGb;
        // Anime collections commonly omit Sxx/Exx from the outer torrent name and reveal their real shape
        // only in the internal file tree. Treat that ambiguity as a provisional pack regardless of its
        // reported total size; metadata preflight applies the byte ceiling to the selected files, not to
        // unrelated arcs that will remain at priority zero.
        bool unscopedAnimeCollection = job.IsAnime
            && job.MediaType is MediaType.TvShow or MediaType.Anime
            && sourceSeason is null && sourceEpisode is null
            && !parsed.LooksLikeCompleteSeries;
        bool declaredCompleteAnimeCollection = job.IsAnime && parsed.LooksLikeCompleteSeries
            && sourceSeason is null && sourceEpisode is null;
        bool unverifiedAnimeCollection = unscopedAnimeCollection || declaredCompleteAnimeCollection;
        bool isPack = sourceEpisode is null && (parsed.IsSeasonPack || parsed.LooksLikeCompleteSeries
            || sourceSeason is not null || unscopedAnimeCollection);
        var canonicalCoverage = new List<EpisodeRef>();
        int? season = sourceSeason;
        int? episode = sourceEpisode;
        int? seasonEnd = parsed.SeasonEnd;
        int? episodeStart = parsed.EpisodeStart;
        int? episodeEnd = parsed.EpisodeEnd;

        if (seasonIdentity is not null)
        {
            season = seasonIdentity.CanonicalSeason;
            seasonEnd = null;
        }

        if (EpisodeOrderMapping.IsActive(job.EpisodeOrderProfile))
        {
            var sourceEpisodes = (c.Episode is int supplied ? new[] { supplied } : parsed.EpisodeNumbers)
                .Distinct().OrderBy(x => x).ToList();
            if (sourceEpisodes.Count > 0 && sourceSeason is int ss)
            {
                foreach (var source in sourceEpisodes)
                {
                    if (EpisodeOrderMapping.TryTranslate(job.EpisodeOrderProfile, ss, source, out var target))
                        canonicalCoverage.Add(target);
                    else
                        rejections.Add(new Rejection(RejectionReason.EpisodeMappingMissing,
                            $"{(ss == 0 ? $"A{source}" : $"S{ss:D2}E{source:D2}")} has no canonical episode mapping"));
                }
            }
            else if (isPack)
            {
                canonicalCoverage.AddRange(EpisodeOrderMapping.SourceSeasonCoverage(job.EpisodeOrderProfile!, sourceSeason));
                if (canonicalCoverage.Count == 0)
                    rejections.Add(new Rejection(RejectionReason.EpisodeMappingMissing,
                        "the pack's source numbering has no canonical episode mappings"));
            }
            else
                rejections.Add(new Rejection(RejectionReason.EpisodeMappingMissing,
                    "the release name has no source episode number to translate"));

            if (canonicalCoverage.Count > 0)
            {
                canonicalCoverage = canonicalCoverage.DistinctBy(x => (x.Season, x.Episode))
                    .OrderBy(x => x.Season).ThenBy(x => x.Episode).ToList();
                season = canonicalCoverage[0].Season;
                seasonEnd = canonicalCoverage[^1].Season == season ? null : canonicalCoverage[^1].Season;
                episode = !isPack && canonicalCoverage.Count == 1 ? canonicalCoverage[0].Episode : null;
                var sameSeason = canonicalCoverage.All(x => x.Season == season);
                episodeStart = sameSeason ? canonicalCoverage[0].Episode : null;
                episodeEnd = sameSeason ? canonicalCoverage[^1].Episode : null;
            }
        }
        else if (sourceSeason is int identitySeason)
        {
            var canonicalSeason = seasonIdentity?.CanonicalSeason ?? identitySeason;
            var sourceEpisodes = (c.Episode is int supplied ? new[] { supplied } : parsed.EpisodeNumbers)
                .Distinct().OrderBy(x => x).ToList();
            canonicalCoverage.AddRange(sourceEpisodes.Select(x => new EpisodeRef { Season = canonicalSeason, Episode = x }));
        }

        if (unverifiedAnimeCollection)
            rejections.Add(new Rejection(RejectionReason.PackScopeUnknown,
                declaredCompleteAnimeCollection
                    ? "anime release claims to be a complete series, but its internal episode manifest must prove that coverage"
                    : c.SizeKnown
                        ? $"{c.SizeGb:F1} GB anime collection has no season/episode scope; map and verify its internal file manifest before selecting it"
                        : "anime collection has no season/episode scope or trustworthy size; map and verify its internal file manifest before selecting it"));

        // ---- Seeders, size ---------------------------------------------------------------------------
        int minSeeders = context.Profile?.MinSeeders ?? p.MinSeeders;
        if (c.SeedersKnown && c.Seeders < minSeeders)
            rejections.Add(new Rejection(RejectionReason.TooFewSeeders, $"{c.Seeders} seeders, minimum is {minSeeders}"));

        double maxSize = isPack ? packLimit : singleFileLimit;

        if (c.SizeKnown)
        {
            if (c.SizeGb < 0.05)
                rejections.Add(new Rejection(RejectionReason.SizeTooSmall, $"{c.SizeGb:F2} GB looks like a fake or empty torrent"));
            // An unscoped/complete anime collection may be much larger than the requested subset. Its
            // content-addressed manifest is the authority for selected bytes; every other release still
            // uses the outer total here.
            else if (c.SizeGb > maxSize && !unverifiedAnimeCollection)
                rejections.Add(new Rejection(RejectionReason.SizeTooLarge, $"{c.SizeGb:F1} GB exceeds the {maxSize:F0} GB limit"));
        }
        // Size unknown is NOT a rejection. The HTML scrapers frequently fail to parse it, and treating that
        // as "0 bytes" silently discarded a large share of their results against the minimum-size gate.

        // ---- Identity --------------------------------------------------------------------------------
        var (idMatch, idMismatch) = CompareImdb(job.ImdbId, c.ImdbId);
        var identityReleaseTitle = NormalizeIdentityTitle(parsed.Title, job.IsAnime);
        var identityJobTitle = NormalizeIdentityTitle(job.Title, job.IsAnime);
        double titleRecall = TitleSimilarity(identityReleaseTitle, identityJobTitle);
        int extraTokens = ExtraTitleTokens(identityReleaseTitle, identityJobTitle);
        int jobRawTokens = RawTokenCount(job.Title);
        // Tolerance for extra words scales with how specific the request is: a one-word title like "Lucky"
        // tolerates none (so it rejects "Lucky Star"), while "The Office" tolerates one (so it accepts the
        // regional variant "The Office US").
        int maxExtra = jobRawTokens <= 1 ? 0 : jobRawTokens <= 3 ? 1 : 2;

        if (idMismatch)
            rejections.Add(new Rejection(RejectionReason.ImdbMismatch, $"IMDb {c.ImdbId} is a different title to {job.ImdbId}"));
        else if (!idMatch && seasonIdentity is null)
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
        var sourceId = c.Acquisition.Protocol == AcquisitionProtocol.Torrent
            ? MagnetUtil.Normalize(c.Acquisition.SourceId) ?? MagnetUtil.InfoHashFromMagnet(c.Acquisition.Locator)
            : c.Acquisition.SourceId;
        if (sourceId is not null && (context.BlocklistedHashes.Contains(sourceId)
            || context.BlocklistedHashes.Contains(AcquisitionResource.BlocklistKey(c.Acquisition.Protocol, sourceId))))
            rejections.Add(new Rejection(RejectionReason.Blocklisted, "this release already failed for this request"));

        if (string.IsNullOrWhiteSpace(c.Acquisition.Locator))
            rejections.Add(new Rejection(RejectionReason.MissingAcquisition, "no acquisition locator"));

        // Custom formats: a user-defined score on top of the structural one, and a floor the profile can
        // set so "never take anything scoring below X" is expressible.
        var (formatScore, matchedFormats) = context.CustomFormats.Count == 0
            ? (0, new List<string>())
            : CustomFormatMatcher.Score(parsed, c, context.CustomFormats, context.CustomFormatScores);

        if (context.Profile is { MinCustomFormatScore: var minScore } && minScore != 0 && formatScore < minScore)
            rejections.Add(new Rejection(RejectionReason.CustomFormatScoreTooLow,
                $"custom-format score {formatScore} is below the profile's minimum of {minScore}"));

        var allowedLanguages = job.MediaLanguagePolicy?.AllowedAudioLanguages.Count > 0
            ? job.MediaLanguagePolicy.AllowedAudioLanguages.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : MediaLanguagePolicy.ParseCsv(context.Profile?.AllowedLanguagesCsv)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowedLanguages.Count > 0 && parsed.Languages.Count > 0)
        {
            // Release names are hints, not proof. Normalize explicit language names/codes so "Japanese"
            // matches "ja", but do not reject ambiguous "dual"/"multi" tags. MediaInfo still enforces
            // the complete allowlist against the actual audio streams before any library write.
            var explicitLanguages = parsed.Languages.Select(MediaLanguagePolicy.NormalizeExplicitLanguage)
                .Where(x => x is not null).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (allowedLanguages.Count > 0 && explicitLanguages.Count > 0
                && !explicitLanguages.Any(allowedLanguages.Contains))
                rejections.Add(new Rejection(RejectionReason.LanguageNotAllowed,
                    $"explicit language hint(s) [{string.Join(", ", explicitLanguages)}] aren't in the profile's allowed set"));
        }

        if (languagePreference == ReleaseLanguagePreference.EnglishOnly)
        {
            var explicitLanguages = parsed.Languages.Select(MediaLanguagePolicy.NormalizeExplicitLanguage)
                .Where(x => x is not null).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (explicitLanguages.Count > 0 && !explicitLanguages.Contains(preferredAudio, StringComparer.OrdinalIgnoreCase)
                && !parsed.MultiLanguage)
                rejections.Add(new Rejection(RejectionReason.LanguageNotAllowed,
                    $"release advertises [{string.Join(", ", explicitLanguages)}], not required {preferredAudio} audio"));
        }

        if (isAnime && languagePreference == ReleaseLanguagePreference.Smart)
        {
            var explicitLanguages = parsed.Languages.Select(MediaLanguagePolicy.NormalizeExplicitLanguage)
                .Where(language => language is not null).Select(language => language!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var explicitlyWrongDub = explicitLanguages.Contains("ja")
                && !explicitLanguages.Contains(preferredAudio)
                && explicitLanguages.Any(language => language != "ja" && language != preferredAudio);
            if (explicitlyWrongDub)
                rejections.Add(new Rejection(RejectionReason.LanguageNotAllowed,
                    $"Smart anime release explicitly advertises Japanese plus " +
                    $"[{string.Join(", ", explicitLanguages.Where(language => language != "ja").Order())}], " +
                    $"but no preferred {preferredAudio} track; wait for {preferredAudio}/Japanese dual audio or Japanese with {preferredAudio} subtitles"));
        }

        var score = Score(c, parsed, resolution, rank, isPack, context, idMatch, titleRecall, formatScore,
            isAnime, job.MediaLanguagePolicy);

        return new RankedCandidate
        {
            Candidate = c,
            Parsed = parsed,
            Resolution = resolution,
            QualityDefinitionId = definition?.Id,
            ProfileRank = rank,
            Season = season,
            SourceSeason = sourceSeason,
            SeasonEnd = seasonEnd,
            Episode = episode,
            EpisodeStart = episodeStart,
            EpisodeEnd = episodeEnd,
            CanonicalEpisodeCoverage = canonicalCoverage,
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
        RankingContext context, bool idMatch, double titleRecall, int formatScore, bool isAnime,
        MediaLanguagePolicyDto? languagePolicy)
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
        if (prefs.PreferX265 && p.Codec == "x265") Add("HEVC/x265 preference", 45);
        if (prefs.PreferHdr && p.Hdr) Add("HDR", 10);
        foreach (var preference in LanguagePreferenceScore(p, context.Profile, isAnime, languagePolicy))
            Add(preference.Name, preference.Points);
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

    /// <summary>
    /// Filename language markers are hints, so normal preferences only affect ordering. Hard requirements
    /// are still proved from the actual streams during import. Smart deliberately has a graceful ladder:
    /// English first for ordinary media; dual audio first for anime, then Japanese with subtitles, then a
    /// usable dub or original-language copy rather than no download at all.
    /// </summary>
    internal static IReadOnlyList<ScoreComponent> LanguagePreferenceScore(
        ParsedRelease release, QualityProfileDto? profile, bool isAnime,
        MediaLanguagePolicyDto? languagePolicy = null)
    {
        var preference = languagePolicy?.Preference ?? profile?.LanguagePreference ?? ReleaseLanguagePreference.Any;
        if (preference is ReleaseLanguagePreference.Any or ReleaseLanguagePreference.Custom)
            return [];

        var parts = new List<ScoreComponent>();
        var explicitLanguages = release.Languages.Select(MediaLanguagePolicy.NormalizeExplicitLanguage)
            .Where(x => x is not null).Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preferred = MediaLanguagePolicy.Normalize(languagePolicy?.PreferredAudioLanguage
                                                       ?? profile?.PreferredAudioLanguage) ?? "en";
        bool hasPreferred = explicitLanguages.Contains(preferred);
        bool hasJapanese = explicitLanguages.Contains("ja");
        bool hasOther = explicitLanguages.Any(x => !x.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        bool looksDual = release.MultiLanguage || explicitLanguages.Count > 1;

        void Add(string name, double points) => parts.Add(new ScoreComponent(name, points));

        switch (preference)
        {
            case ReleaseLanguagePreference.Smart when isAnime:
                if (looksDual && hasPreferred && hasJapanese) Add("Smart anime: dual audio", 240);
                else if (hasJapanese && release.Subbed) Add("Smart anime: original audio with subtitles", 170);
                else if (hasPreferred) Add("Smart anime: preferred-language dub", 120);
                else if (hasJapanese) Add("Smart anime: original audio fallback", 40);
                else if (explicitLanguages.Count == 0) Add("Smart anime: unlabelled fallback", 25);
                break;
            case ReleaseLanguagePreference.Smart:
            case ReleaseLanguagePreference.EnglishPreferred:
                if (hasPreferred) Add("Preferred audio language", hasOther ? 80 : 180);
                // English-language scene releases usually omit ENG entirely; an unlabelled release should
                // outrank one explicitly advertising a foreign-first multilingual edition.
                else if (explicitLanguages.Count == 0) Add("Unlabelled/default-language release", 120);
                else Add("Original-language fallback", -80);
                break;
            case ReleaseLanguagePreference.EnglishOnly:
                if (hasPreferred) Add("Required audio language hint", 80);
                break;
            case ReleaseLanguagePreference.OriginalWithEnglishSubtitles:
                if (isAnime && hasJapanese) Add("Original Japanese audio", 70);
                else if (hasOther) Add("Original-language audio", 50);
                if (release.Subbed) Add("Preferred subtitles", 35);
                break;
            case ReleaseLanguagePreference.DualAudioPreferred:
                if (looksDual) Add("Dual audio", 100);
                if (hasPreferred) Add("Preferred audio language", 35);
                break;
        }

        if ((languagePolicy?.PreferForcedSubtitles ?? profile?.PreferForcedSubtitles) == true
            && release.ForcedSubtitles)
            Add("Forced subtitles", 30);
        return parts;
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

    private static string NormalizeIdentityTitle(string title, bool isAnime)
    {
        if (!isAnime || string.IsNullOrWhiteSpace(title)) return title;
        // Nyaa's conventional leading [release group] is provenance, not title identity. "Series" is also
        // routinely appended to franchise names ("Monogatari Series") without being part of the requested
        // metadata title. Keep this anime-only so titles in other media domains retain their literal words.
        var normalized = Regex.Replace(title, @"^\s*\[[^\[\]\r\n]{1,64}\]\s*", string.Empty);
        normalized = Regex.Replace(normalized, @"\bseries\b", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(normalized, @"\s+", " ").Trim();
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
