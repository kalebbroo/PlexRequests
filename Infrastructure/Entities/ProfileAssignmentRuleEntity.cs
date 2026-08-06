using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// Picks a <see cref="QualityProfileEntity"/> for a title based on what it is. First enabled rule whose
/// conditions all match wins; if none match, the requester's own choice or the default profile applies.
///
/// This is the surviving half of the old <c>QualityRuleEntity</c>: its condition columns are carried over
/// unchanged (so "kids' cartoons -> 720p" style intent is preserved), while the target it points at is now a
/// full profile instead of a single quality value.
///
/// A null condition means "don't care". A rule with EVERY condition null therefore matches everything, which
/// is why the service rejects that at save: the old panel created new rules enabled, targeting 720p, with no
/// conditions, so a single click silently capped every future download in the system at 720p.
/// </summary>
public class ProfileAssignmentRuleEntity
{
    public int Id { get; set; }

    [MaxLength(128)] public string Name { get; set; } = string.Empty;

    /// <summary>Evaluation order; lowest first. First match wins.</summary>
    public int Order { get; set; }

    /// <summary>New rules are created disabled so a half-configured rule can't take effect immediately.</summary>
    public bool Enabled { get; set; }

    // ---- Conditions (null = any) -------------------------------------------------------------------
    public MediaType? MatchMediaType { get; set; }
    /// <summary>Case-insensitive match against the title's genre list.</summary>
    [MaxLength(128)] public string? MatchGenre { get; set; }
    /// <summary>Pin one specific title by its TMDb id.</summary>
    public int? MatchTmdbId { get; set; }
    /// <summary>Plex library section name.</summary>
    [MaxLength(256)] public string? MatchLibrary { get; set; }

    /// <summary>The profile applied when this rule matches.</summary>
    public int QualityProfileId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>True when no condition is set, i.e. this rule would match every title. Rejected at save.</summary>
    public bool IsUnconditional =>
        MatchMediaType is null && MatchTmdbId is null
        && string.IsNullOrWhiteSpace(MatchGenre) && string.IsNullOrWhiteSpace(MatchLibrary);
}
