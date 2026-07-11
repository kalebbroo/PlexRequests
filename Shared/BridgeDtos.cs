using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared.DTOs;

// Wire types for the Discord bridge API (/api/bridge/*). The bot/extension keeps its own copies
// of these shapes on the far side of the HTTP boundary.

public class BridgeSearchResultDto
{
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
    public BridgeEventType Type { get; set; }
    public int RequestId { get; set; }
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
    public RequestStatus Status { get; set; }
    public string? Detail { get; set; }
    public string? RequesterName { get; set; }
    /// <summary>Set only if the requester is Discord-linked AND opted in to DMs — safe to DM.</summary>
    public string? RequesterDiscordId { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ---- request bodies ----
public record BridgeRequestBody(string DiscordUserId, int MediaId, MediaType MediaType);
public record BridgeLinkBody(string Code, string DiscordUserId, string? DiscordUsername);
public record BridgeAdminActionBody(string DiscordUserId, string? Reason);
