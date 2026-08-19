using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// One row per request-lifecycle transition, consumed by the Discord bridge extension via
/// GET /api/bridge/events/v3?since=&lt;cursor&gt;. The auto-increment Id is the cursor. Request identity and
/// lifecycle state are immutable snapshots; optional rich metadata and the current opted-in Discord link
/// are resolved at read time.
/// </summary>
public class BridgeOutboxEntity
{
    public long Id { get; set; }

    /// <summary>Stable idempotency key, unique across retries and repair passes.</summary>
    [MaxLength(96)]
    public string DeduplicationKey { get; set; } = string.Empty;

    public BridgeEventType EventType { get; set; }

    public int MediaRequestId { get; set; }
    public int MediaId { get; set; }
    public MediaType MediaType { get; set; }
    public MediaKind MediaKind { get; set; }
    public RequestScopeKind RequestScopeKind { get; set; }
    [MaxLength(32)] public string? ExternalSource { get; set; }
    [MaxLength(128)] public string? ExternalId { get; set; }

    [MaxLength(512)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? PosterUrl { get; set; }

    public RequestStatus Status { get; set; }

    public int? RequesterUserId { get; set; }
    [MaxLength(128)]
    public string? RequesterName { get; set; }

    [MaxLength(1000)]
    public string? Detail { get; set; } // e.g. denial/failure reason

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AcknowledgedAt { get; set; }

    [ForeignKey(nameof(MediaRequestId))]
    public MediaRequestEntity? MediaRequest { get; set; }
}
