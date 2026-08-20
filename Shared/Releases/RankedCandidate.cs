using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Shared.Releases;

/// <summary>Why a release was not acceptable. Each value is a distinct, explainable cause.</summary>
public enum RejectionReason
{
    /// <summary>Below the profile's quality floor while the floor is still being enforced.</summary>
    BelowQualityFloor,
    /// <summary>The release's quality tier isn't in the profile's allowed list.</summary>
    NotInProfile,
    /// <summary>The release's quality tier ranks above the profile's cutoff — the cutoff is a ceiling, not
    /// just a point where auto-upgrading stops, so this is not "better", it's not what was asked for.</summary>
    AboveCutoff,
    /// <summary>An upgrade job found something no better than what's already in the library.</summary>
    NotAnUpgrade,
    TooFewSeeders,
    SizeTooSmall,
    SizeTooLarge,
    TitleMismatch,
    /// <summary>The release's core title carries significant words the request doesn't — a different, longer title.</summary>
    ExtraTitleTokens,
    ImdbMismatch,
    YearMismatch,
    MediaTypeMismatch,
    ArtistMismatch,
    MusicFormatMissing,
    CatalogScopeMismatch,
    MetadataIncomplete,
    WrongSeason,
    WrongEpisode,
    /// <summary>A season pack that demonstrably doesn't cover the episodes still missing.</summary>
    PackIncomplete,
    Blocklisted,
    /// <summary>Scored below the profile's minimum custom-format score.</summary>
    CustomFormatScoreTooLow,
    LanguageNotAllowed,
    TooOld,
    MissingAcquisition
}

/// <summary>One reason a release was rejected, with enough detail to explain it to a human.</summary>
public sealed record Rejection(RejectionReason Reason, string Detail);

/// <summary>One contribution to a release's score. Makes "why did that one win" answerable.</summary>
public sealed record ScoreComponent(string Name, double Points);

/// <summary>
/// A release after evaluation: what it parsed to, whether it's acceptable, why not if not, and how it
/// scored broken down by contribution.
///
/// Returning rejections and score components as DATA rather than log lines is the point. It keeps this
/// assembly dependency-free (no logger), it makes the previously invisible per-candidate <c>LogDebug</c>
/// rejections presentable in the UI, and it's what an interactive search needs to show an admin why the
/// release they were expecting wasn't picked.
/// </summary>
public sealed record RankedCandidate
{
    public required ReleaseCandidate Candidate { get; init; }
    public required ParsedRelease Parsed { get; init; }

    /// <summary>Effective vertical resolution: the indexer's own label when it gave one, else parsed.</summary>
    public int Resolution { get; init; }
    /// <summary>The quality tier this release resolved to, or null when the catalog has no matching row.</summary>
    public int? QualityDefinitionId { get; init; }
    /// <summary>Rank of that tier within the profile (higher is better); null when it isn't in the profile.</summary>
    public int? ProfileRank { get; init; }

    public int? Season { get; init; }
    public int? SeasonEnd { get; init; }
    public int? Episode { get; init; }
    /// <summary>First and last episode when the name declares a range (S01E01-E06). Null otherwise.</summary>
    public int? EpisodeStart { get; init; }
    public int? EpisodeEnd { get; init; }
    public bool IsPack { get; init; }
    public bool LooksLikeCompleteSeries { get; init; }

    public bool Accepted { get; init; }
    public IReadOnlyList<Rejection> Rejections { get; init; } = Array.Empty<Rejection>();

    public double Score { get; init; }
    public IReadOnlyList<ScoreComponent> ScoreBreakdown { get; init; } = Array.Empty<ScoreComponent>();

    /// <summary>Custom-format score. Always 0 until custom formats ship.</summary>
    public int CustomFormatScore { get; init; }
    public IReadOnlyList<string> MatchedFormats { get; init; } = Array.Empty<string>();

    public string Summary => Accepted
        ? $"{Candidate.ReleaseName} ({Resolution}p, score {Score:F0})"
        : $"{Candidate.ReleaseName} — {string.Join("; ", Rejections.Select(r => r.Detail))}";
}

/// <summary>
/// Everything the evaluator needs to judge a release, assembled by the caller. A plain data bag on purpose:
/// it crosses the process boundary as part of the job payload, and keeping it free of services is what lets
/// the same evaluation run in the downloader (automatic search) and the web app (interactive search).
/// </summary>
public sealed class RankingContext
{
    /// <summary>Global admin download preferences (seeders, sizes, title similarity, strategy).</summary>
    public required DownloadPreferencesDto Preferences { get; init; }

    /// <summary>The request's quality profile. Null falls back to the job's flat resolution floor.</summary>
    public QualityProfileDto? Profile { get; init; }

    /// <summary>The tier catalog, needed to resolve a release's (resolution, source) to a tier id.</summary>
    public IReadOnlyList<QualityDefinitionDto> Definitions { get; init; } = Array.Empty<QualityDefinitionDto>();

    /// <summary>Priority (1 best – 50 worst) per indexer id; used only to break near-ties.</summary>
    public IReadOnlyDictionary<int, int> IndexerPriorities { get; init; } = new Dictionary<int, int>();

    /// <summary>Enabled custom formats to evaluate against each release.</summary>
    public IReadOnlyList<CustomFormatDto> CustomFormats { get; init; } = Array.Empty<CustomFormatDto>();

    /// <summary>What each format is worth in THIS request's profile, keyed by format id.</summary>
    public IReadOnlyDictionary<int, int> CustomFormatScores { get; init; } = new Dictionary<int, int>();

    /// <summary>Info hashes already known to have failed for this request; never grab them again.</summary>
    public IReadOnlySet<string> BlocklistedHashes { get; init; } = new HashSet<string>();

    /// <summary>
    /// True once the quality floor has been held out for long enough and we should take the best available
    /// release instead. Decided by the caller from the job's empty-search count, not from raw attempts.
    /// </summary>
    public bool RelaxQualityFloor { get; init; }

    /// <summary>Now, injectable so tests and age filters are deterministic.</summary>
    public DateTime UtcNow { get; init; } = DateTime.UtcNow;
}
