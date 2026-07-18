using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// Durable record of one file the organizer placed into the Plex library for a fulfillment job. A job
/// can back many of these (a season pack splits into one row per episode/subtitle) — this is the audit
/// trail that's otherwise entirely absent once the worker-local transient job state is cleaned up.
/// </summary>
public class ImportedFileEntity
{
    public int Id { get; set; }
    public int FulfillmentJobId { get; set; }

    [MaxLength(64)] public string? TorrentId { get; set; }
    [MaxLength(2048)] public string SourcePath { get; set; } = string.Empty;
    [MaxLength(2048)] public string DestinationPath { get; set; } = string.Empty;
    [MaxLength(16)] public string FileType { get; set; } = "video"; // "video" | "subtitle"
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>Vertical resolution (pixel height, e.g. 1080) of the release this file came from, as ranked
    /// by the downloader. 0 = unknown. Drives achieved-quality rollup and quality-upgrade (cutoff) detection.</summary>
    public int ResolutionHeight { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(FulfillmentJobId))]
    public FulfillmentJobEntity? FulfillmentJob { get; set; }
}
