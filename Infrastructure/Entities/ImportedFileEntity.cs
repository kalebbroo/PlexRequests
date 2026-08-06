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

    /// <summary>The full tier (resolution x source) this file's release matched, as a
    /// <see cref="QualityDefinitionEntity"/> id. Null for rows imported before tiers existed.</summary>
    public int? QualityDefinitionId { get; set; }

    /// <summary>Custom-format score of the release this file came from. Inert until custom formats ship.</summary>
    public int CustomFormatScore { get; set; }

    /// <summary>
    /// The release name this file was imported from. Worth storing because it lets quality, source and (later)
    /// custom-format scores be re-derived for the existing library without re-searching indexers — which is
    /// what makes a cutoff state of "unknown" recoverable and a scoring-rule change retroactive.
    /// </summary>
    [MaxLength(512)] public string? ReleaseName { get; set; }

    /// <summary>Info hash of the torrent this came from, for failure blocklisting.</summary>
    [MaxLength(40)] public string? InfoHash { get; set; }

    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(FulfillmentJobId))]
    public FulfillmentJobEntity? FulfillmentJob { get; set; }
}
