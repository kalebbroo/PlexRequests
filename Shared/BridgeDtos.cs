using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Shared.DTOs;

// Wire types for the Discord bridge API (/api/bridge/*). The bot/extension keeps its own copies
// of these shapes on the far side of the HTTP boundary.

public class BridgeSearchResultDto
{
    public MediaRef? Media { get; set; }
    public int MediaId { get; set; }
    public MediaType MediaType { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? Overview { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public decimal? Rating { get; set; }
    public int? TmdbId { get; set; }
    public List<string> Genres { get; set; } = new();
    public bool AvailableOnPlex { get; set; }
    /// <summary>The requesting Discord user's current status for this title, if linked (else null).</summary>
    public RequestStatus? RequestStatus { get; set; }
}

public class BridgeRequestResultDto
{
    public bool Success { get; set; }
    public int? RequestId { get; set; }
    public RequestStatus? Status { get; set; }
    public string? Message { get; set; }
    /// <summary>True when the failure is because the Discord user isn't linked yet.</summary>
    public bool NotLinked { get; set; }
}

public class BridgeUserRequestDto
{
    public int RequestId { get; set; }
    public string Title { get; set; } = string.Empty;
    public MediaType MediaType { get; set; }
    public MediaRef? Media { get; set; }
    public RequestScopeKind RequestScope { get; set; }
    public RequestStatus Status { get; set; }
    public string? PosterUrl { get; set; }
    public DateTime RequestedAt { get; set; }
    public string? DenialReason { get; set; }
}

public class BridgeUserRequestsDto
{
    public bool Linked { get; set; }
    public List<BridgeUserRequestDto> Items { get; set; } = new();
}

public class BridgeLinkResultDto
{
    public bool Success { get; set; }
    public string? PlexUsername { get; set; }
    public string? Message { get; set; }
}

public class BridgeLinkStatusDto
{
    public bool Linked { get; set; }
    public string? PlexUsername { get; set; }
    public bool DmOptIn { get; set; }
}

public class BridgeEventDto
{
    /// <summary>Cursor — pass the highest value seen back as `since` on the next poll.</summary>
    public long Cursor { get; set; }
    /// <summary>Stable idempotency key. PlexBox may safely ignore a repeated event with this id.</summary>
    public string EventId { get; set; } = string.Empty;
    public BridgeEventType Type { get; set; }
    public int RequestId { get; set; }
    public int MediaId { get; set; }
    public MediaType MediaType { get; set; }
    public MediaRef? Media { get; set; }
    public RequestScopeKind RequestScope { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? Overview { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public decimal? Rating { get; set; }
    public int? TmdbId { get; set; }
    public List<string> Genres { get; set; } = new();
    public RequestStatus Status { get; set; }
    public string? Detail { get; set; }
    public string? RequesterName { get; set; }
    /// <summary>Set only if the requester is Discord-linked AND opted in to DMs — safe to DM.</summary>
    public string? RequesterDiscordId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class BridgeCapabilitiesDto
{
    public int ApiVersion { get; set; } = 3;
    public List<MediaModuleDescriptor> MediaModules { get; set; } = new();
    public bool SupportsEventBatches { get; set; } = true;
    public bool SupportsEventAcknowledgement { get; set; } = true;
    public int EventRetentionDays { get; set; } = 30;
}

/// <summary>Version 3 event envelope. The legacy array endpoint remains available for older PlexBox builds.</summary>
public sealed class BridgeEventBatchDto
{
    public int ApiVersion { get; set; } = 3;
    public long RequestedCursor { get; set; }
    public long OldestAvailableCursor { get; set; }
    public long NextCursor { get; set; }
    public bool HasMore { get; set; }
    /// <summary>The supplied cursor predates retained history. Current request states were repaired into
    /// the feed, but the consumer should treat this as a state resynchronization rather than full history.</summary>
    public bool CursorExpired { get; set; }
    public DateTime ServerTimeUtc { get; set; } = DateTime.UtcNow;
    public List<BridgeEventDto> Events { get; set; } = new();
}

// ---- request bodies ----
public sealed class BridgeRequestBody
{
    public string DiscordUserId { get; set; } = string.Empty;
    /// <summary>Version 2 identity. Preferred over the legacy integer fields below.</summary>
    public MediaRef? Media { get; set; }
    public MediaRequestScope? Scope { get; set; }
    public int? QualityProfileId { get; set; }

    // Version 1 compatibility. PlexBox can upgrade independently without a flag day.
    public int MediaId { get; set; }
    public MediaType MediaType { get; set; }

    public MediaRef ResolveMedia() => Media is { IsValid: true }
        ? Media
        : MediaRef.FromTmdb(MediaId, MediaType);
}
public record BridgeLinkBody(string Code, string DiscordUserId, string? DiscordUsername);
public record BridgeAdminActionBody(string DiscordUserId, string? Reason);
public record BridgeEventAckBody(long Cursor);
public record BridgeEventAckResult(long Cursor, int Acknowledged, int Pruned);
