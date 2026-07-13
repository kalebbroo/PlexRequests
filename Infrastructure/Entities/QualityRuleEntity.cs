using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// An admin-defined quality rule. Rules are evaluated in <see cref="Order"/>; the first enabled rule
/// whose set conditions ALL match a title wins, and its <see cref="TargetQuality"/> is applied to the
/// download job. The single <see cref="IsDefault"/> rule is the catch-all when nothing else matches.
/// Conditions left null mean "any" (e.g. a rule with only MatchGenre="Animation" + MatchMediaType=TvShow
/// = "kids cartoons -> 720p"). Not exposed to end users.
/// </summary>
public class QualityRuleEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [MaxLength(128)] public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsDefault { get; set; }

    // Match conditions (null = any). A rule matches when every non-null condition matches.
    public MediaType? MatchMediaType { get; set; }
    [MaxLength(128)] public string? MatchGenre { get; set; }   // case-insensitive; title must contain this genre
    public int? MatchTmdbId { get; set; }                      // a specific title
    [MaxLength(256)] public string? MatchLibrary { get; set; } // Plex library/section name (optional)

    public Quality TargetQuality { get; set; } = Quality.FullHD;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
