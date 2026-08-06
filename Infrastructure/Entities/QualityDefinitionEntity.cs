using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// One quality tier: a (resolution, source) pair such as "WEBDL-1080p" or "Bluray-720p". This is the
/// vocabulary a <see cref="QualityProfileEntity"/> orders and picks a cutoff from.
///
/// Resolution alone isn't enough to express what people actually want — a 1080p WEB-DL and a 1080p CAM are
/// not interchangeable — which is why the tier is two-dimensional. Both axes reuse existing enums
/// (<see cref="Quality"/> for the pixel height, <see cref="ReleaseSource"/> for the source, already ordered
/// worst-to-best), so nothing about the existing height-based comparisons had to change.
///
/// The catalog is seeded and effectively fixed; admins order and select tiers in a profile rather than
/// inventing new ones.
/// </summary>
public class QualityDefinitionEntity
{
    public int Id { get; set; }

    /// <summary>Display name, e.g. "WEBDL-1080p". Generated at seed time from the two axes.</summary>
    [MaxLength(64)] public string Name { get; set; } = string.Empty;

    /// <summary>Vertical resolution in pixels — the <see cref="Quality"/> enum's own value (480/720/1080/...).</summary>
    public int Resolution { get; set; }

    public ReleaseSource Source { get; set; }

    /// <summary>
    /// Total ordering across the catalog: <c>Resolution * 10 + (int)Source</c>. Resolution dominates (a 720p
    /// Remux ranks below a 1080p WEBRip), with the source breaking ties inside a resolution. Persisted rather
    /// than computed so profile ordering can be done in SQL.
    /// </summary>
    public int SortWeight { get; set; }

    /// <summary>Seeded by the app. System rows are never deleted, so a profile can't end up referencing a
    /// tier that no longer exists.</summary>
    public bool IsSystem { get; set; } = true;

    /// <summary>Optional per-tier size sanity limits, in GB. Null = fall through to the profile, then to the
    /// global download preferences. A 2160p Remux legitimately dwarfs a 480p HDTV rip, so a single global
    /// cap is always either too tight at the top or useless at the bottom.</summary>
    public double? MinSizeGb { get; set; }
    public double? MaxSizeGb { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
