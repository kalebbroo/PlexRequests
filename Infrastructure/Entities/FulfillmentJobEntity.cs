using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// A unit of work handed to the out-of-process downloader when an admin approves a request.
/// The web app only enqueues and tracks state; the downloader claims jobs and reports back via
/// the secured /api/fulfillment and /api/requests/{id}/(fulfilled|failed) endpoints.
/// </summary>
public class FulfillmentJobEntity
{
    public int Id { get; set; }

    public int MediaRequestId { get; set; }

    public int MediaId { get; set; }
    public MediaType MediaType { get; set; }

    [MaxLength(512)]
    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    // External identifiers for indexer search (MediaId is the TMDb id for the default provider).
    public int? TmdbId { get; set; }
    [MaxLength(32)]
    public string? ImdbId { get; set; }
    public int? TvdbId { get; set; }

    [MaxLength(256)]
    public string? RequestedSeasonsCsv { get; set; }

    // Episode-level targets, format "S1E1,S2E5". Empty ⇒ fall back to seasons/whole title.
    [MaxLength(4096)]
    public string? RequestedEpisodesCsv { get; set; }

    public Quality Quality { get; set; }

    public FulfillmentStatus Status { get; set; } = FulfillmentStatus.Queued;
    public int Attempts { get; set; }
    public int Progress { get; set; } // 0-100

    [MaxLength(128)]
    public string? ClaimedBy { get; set; }

    [MaxLength(2000)]
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClaimedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>Last time the worker touched this job (claim/progress). Used by the stale-claim reaper.</summary>
    public DateTime? LastUpdatedAt { get; set; }

    [ForeignKey(nameof(MediaRequestId))]
    public MediaRequestEntity? MediaRequest { get; set; }
}
