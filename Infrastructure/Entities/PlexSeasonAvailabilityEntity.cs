using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// Per-season availability of a TV show on Plex. One row per (show ratingKey, season number).
/// <see cref="AvailableEpisodesCsv"/> lists the episode numbers actually present so we can dedup at
/// episode granularity. Rows are upserted by the availability refresh scan and pruned when a season
/// is no longer seen on the server.
/// </summary>
public class PlexSeasonAvailabilityEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Plex ratingKey of the parent show (matches <see cref="PlexMappingEntity.RatingKey"/>).</summary>
    [Required]
    [MaxLength(64)]
    public string ShowRatingKey { get; set; } = string.Empty;

    public int SeasonNumber { get; set; }

    /// <summary>Episode numbers present on Plex for this season, e.g. "1,2,3,5". Empty ⇒ none/unknown.</summary>
    [MaxLength(4096)]
    public string AvailableEpisodesCsv { get; set; } = string.Empty;

    /// <summary>Count of episodes available (denormalized for quick reads).</summary>
    public int EpisodeCount { get; set; }

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
