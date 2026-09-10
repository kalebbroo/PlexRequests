using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// One exact media part reported by Plex's own library scan. This inventory is read-only: it records what
/// Plex currently sees so older library files can eventually enter the same preview-first optimization
/// workflow as Plex Requests imports. A physical multi-episode file can appear under more than one Plex
/// rating key; consumers must group by <see cref="FilePath"/> before counting physical storage.
/// </summary>
public sealed class PlexLibraryFileEntity
{
    public int Id { get; set; }

    [MaxLength(64)] public string SectionKey { get; set; } = string.Empty;
    [MaxLength(128)] public string SectionTitle { get; set; } = string.Empty;
    [MaxLength(64)] public string RatingKey { get; set; } = string.Empty;
    [MaxLength(64)] public string? ShowRatingKey { get; set; }
    /// <summary>Plex Part.id, or a stable path digest when an older response omits it.</summary>
    [MaxLength(128)] public string PlexPartKey { get; set; } = string.Empty;
    [MaxLength(2048)] public string FilePath { get; set; } = string.Empty;

    public MediaType MediaType { get; set; }
    [MaxLength(512)] public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }

    public long SizeBytes { get; set; }
    public int ResolutionHeight { get; set; }
    [MaxLength(32)] public string? VideoCodec { get; set; }
    [MaxLength(32)] public string? AudioCodec { get; set; }

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    /// <summary>Consecutive trustworthy Plex scans that did not contain this part.</summary>
    public int MissedScans { get; set; }
}
