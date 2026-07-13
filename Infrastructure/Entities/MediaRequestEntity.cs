using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

public class MediaRequestEntity
{
    public int Id { get; set; }
    public int MediaId { get; set; }   // TMDb id for movie/TV; 0 for sources without an int id (music)
    public MediaType MediaType { get; set; }

    // Provider-agnostic identifier for non-TMDb sources (e.g. MusicBrainz MBID, Plex ratingKey, TVDB id).
    // TODO(music): music requests key off this instead of MediaId. Thread it through fulfillment + the
    // availability match so an album/artist request can be found and downloaded.
    [MaxLength(128)]
    public string? ExternalId { get; set; }

    [MaxLength(32)]
    public string? ExternalSource { get; set; }   // "musicbrainz" | "plex" | "tvdb" | ... (null = tmdb)

    [MaxLength(512)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? PosterUrl { get; set; }

    public RequestStatus Status { get; set; } = RequestStatus.Pending;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    // Foreign key to UserEntity
    public int? RequestedByUserId { get; set; }

    // Keep for backward compatibility, but should migrate to RequestedByUserId
    [MaxLength(128)]
    public string? RequestedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? AvailableAt { get; set; }

    [MaxLength(1000)]
    public string? DenialReason { get; set; }

    [MaxLength(2000)]
    public string? RequestNote { get; set; }

    // TV specific selection
    public bool RequestAllSeasons { get; set; }

    [MaxLength(256)]
    public string? RequestedSeasonsCsv { get; set; }

    // Episode-level selection, format "S1E1,S1E2,S2E5". Null/empty ⇒ season/series level applies.
    [MaxLength(4096)]
    public string? RequestedEpisodesCsv { get; set; }

    // Ongoing-series monitoring: when set (whole-series request of a still-running show), the
    // SeriesMonitorService keeps checking TMDB for newly-aired episodes and auto-enqueues them.
    public bool Monitored { get; set; }

    // Navigation property
    [ForeignKey(nameof(RequestedByUserId))]
    public UserEntity? RequestedByUser { get; set; }
}
