using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// A named quality target a request is bound to: which tiers are acceptable, in what order of preference,
/// and the point past which we stop trying to do better ("upgrade until").
///
/// This replaces the old <c>QualityRuleEntity</c> model, which resolved to a single <see cref="Quality"/>
/// value and so could express "I want 1080p" but not "prefer WEBDL-1080p, accept 720p rather than nothing,
/// and keep hunting until you find the former". The conditions that used to select a rule now live in
/// <see cref="ProfileAssignmentRuleEntity"/>, so conditional assignment survives the change.
/// </summary>
public class QualityProfileEntity
{
    public int Id { get; set; }

    [MaxLength(128)] public string Name { get; set; } = string.Empty;

    /// <summary>The catch-all used when no assignment rule matches and the requester didn't pick one.
    /// Exactly one profile carries this.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Whether requesters may choose this profile themselves, subject to their own allow-list.</summary>
    public bool IsUserSelectable { get; set; } = true;

    /// <summary>Which media types this profile may be applied to, as <see cref="MediaTypeFlags"/>.</summary>
    public int AppliesToMediaTypes { get; set; } = (int)MediaTypeFlags.All;

    /// <summary>Display order in the admin list and the requester's picker.</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// The ordered tier list, as JSON. Each entry is either a single quality definition or a named group of
    /// definitions treated as equivalent (WEBDL-1080p and WEBRip-1080p usually are), with an <c>allowed</c>
    /// flag. Index = rank, ascending from worst.
    ///
    /// JSON rather than a child table because the list is always loaded whole and never queried by item,
    /// reordering is a single write instead of an ordering-column dance, and the codebase already stores
    /// owned ordered collections this way (SeasonTargetsJson, EpisodeQualityJson).
    /// Shape: <c>[{"k":"g:web1080","n":"WEB 1080p","allowed":true,"members":[12,11]},{"k":"q:14","n":"Bluray-1080p","allowed":true}]</c>
    /// </summary>
    public string ItemsJson { get; set; } = "[]";

    /// <summary>
    /// Stop upgrading once the library holds this tier or better. Refers to a <see cref="QualityDefinitionEntity"/>.
    /// Anything below it that's still allowed will be grabbed, then replaced when something at/above appears.
    /// </summary>
    public int CutoffQualityDefinitionId { get; set; }

    /// <summary>Off means "take the first acceptable release and never revisit it".</summary>
    public bool UpgradeAllowed { get; set; } = true;

    /// <summary>Custom-format score floors. Inert until custom formats ship; stored now so the profile
    /// schema doesn't need another migration then.</summary>
    public int MinCustomFormatScore { get; set; }
    public int CutoffFormatScore { get; set; }

    /// <summary>Per-profile overrides of the global download preferences. Null = inherit.</summary>
    public double? MinSizeGb { get; set; }
    public double? MaxSizeGb { get; set; }
    public double? MaxSeasonPackSizeGb { get; set; }
    public int? MinSeeders { get; set; }

    /// <summary>Comma-separated ISO audio language allowlist. Null/empty = any. The legacy column name is
    /// retained so existing installations and API clients upgrade without losing their setting.</summary>
    [MaxLength(256)] public string? AllowedLanguagesCsv { get; set; }

    /// <summary>Structured constraints proved from actual streams immediately before import.</summary>
    [MaxLength(256)] public string? RequiredAudioLanguagesCsv { get; set; }
    [MaxLength(256)] public string? RequiredSubtitleLanguagesCsv { get; set; }
    public bool RequireForcedSubtitle { get; set; }
    public bool AllowUnknownTrackLanguage { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Bitmask form of <see cref="MediaType"/>, so a profile can apply to several types at once.</summary>
[Flags]
public enum MediaTypeFlags
{
    None = 0,
    Movie = 1,
    TvShow = 2,
    Music = 4,
    Anime = 8,
    All = Movie | TvShow | Music | Anime
}
