using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// One row per request-lifecycle transition, consumed by the Discord bridge extension via
/// GET /api/bridge/events?since=&lt;cursor&gt;. The auto-increment Id is the cursor. Kept minimal —
/// the events endpoint enriches with fresh artwork/metadata and the requester's Discord link at read time.
/// </summary>
public class BridgeOutboxEntity
{
    public long Id { get; set; }

    public BridgeEventType EventType { get; set; }

    public int MediaRequestId { get; set; }
    public int MediaId { get; set; }
    public MediaType MediaType { get; set; }

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

    [ForeignKey(nameof(MediaRequestId))]
    public MediaRequestEntity? MediaRequest { get; set; }
}
