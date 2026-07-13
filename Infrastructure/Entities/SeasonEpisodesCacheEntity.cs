using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// Persistent cache of a TV season's episode list (from TMDB) — one row per (ShowTmdbId, SeasonNumber).
/// Kept separate from <see cref="MediaMetadataCacheEntity"/> because it refreshes on a different cadence
/// (ongoing shows) and is larger. Stores the serialized List&lt;EpisodeDto&gt; as JSON.
/// </summary>
public class SeasonEpisodesCacheEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int ShowTmdbId { get; set; }   // + SeasonNumber = unique key
    public int SeasonNumber { get; set; }

    public string EpisodesJson { get; set; } = "[]";   // List<EpisodeDto>
    public DateTime FetchedAt { get; set; }
}
