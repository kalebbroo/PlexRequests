namespace PlexRequestsHosted.Shared.DTOs;

/// <summary>A short-lived, single-use code created by an authenticated administrator.</summary>
public sealed class FirefoxCapturePairingDto
{
    public string Code { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}

/// <summary>Sent by the Firefox extension when it exchanges a pairing code for a device token.</summary>
public sealed class FirefoxCapturePairRequestDto
{
    public string PairingCode { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string ExtensionVersion { get; set; } = string.Empty;
}

/// <summary>The raw token is returned exactly once. Only its SHA-256 hash is persisted by the server.</summary>
public sealed class FirefoxCapturePairResponseDto
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public int IndexerId { get; set; }
    public string Implementation { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

/// <summary>
/// One rendered page observation. The server derives the indexer and source from the device token; the
/// extension is deliberately not allowed to choose either security boundary.
/// </summary>
public sealed class FirefoxCaptureBatchDto
{
    public string BatchId { get; set; } = string.Empty;
    public string PageUrl { get; set; } = string.Empty;
    public string PageType { get; set; } = string.Empty;
    public int ParserVersion { get; set; }
    public string ExtensionVersion { get; set; } = string.Empty;
    public DateTime CapturedAt { get; set; }
    public List<CatalogItemDto> Items { get; set; } = new();
}

public sealed class FirefoxCaptureIngestResponseDto
{
    public bool DuplicateBatch { get; set; }
    public int AcceptedItems { get; set; }
    public int ReleasesInserted { get; set; }
    public int SightingsInserted { get; set; }
    public int UnresolvedSightings { get; set; }
}

/// <summary>A rendered detail page that authoritatively says its immutable source id no longer exists.</summary>
public sealed class FirefoxCaptureHydrationFailureDto
{
    public string ExternalId { get; set; } = string.Empty;
    public string PageUrl { get; set; } = string.Empty;
    public string FailureCode { get; set; } = string.Empty;
}

public sealed class FirefoxCaptureConnectionDto
{
    public bool Connected { get; set; }
    public DateTime ServerTime { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string Implementation { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string CurrentExtensionVersion { get; set; } = string.Empty;
}

/// <summary>
/// A privacy-preserving queue snapshot. The bearer token determines the source; browser history, release
/// names, URLs, cookies, and session data are deliberately excluded.
/// </summary>
public sealed class FirefoxCaptureHeartbeatDto
{
    public bool CaptureEnabled { get; set; }
    public int QueuedUploads { get; set; }
    public int FailedUploads { get; set; }
    public int PendingDetails { get; set; }
    public int AttentionDetails { get; set; }
    public DateTime? HydrationPausedUntil { get; set; }
    public string ExtensionVersion { get; set; } = string.Empty;
}

public sealed class FirefoxCaptureDeviceDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ExtensionVersion { get; set; } = string.Empty;
    public bool UpdateAvailable { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? LastCaptureAt { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public bool CaptureEnabled { get; set; }
    public int QueuedUploads { get; set; }
    public int FailedUploads { get; set; }
    public int PendingDetails { get; set; }
    public int AttentionDetails { get; set; }
    public DateTime? HydrationPausedUntil { get; set; }
    public long BatchesReceived { get; set; }
    public long ItemsReceived { get; set; }
    public int? LastParserVersion { get; set; }
    public string? LastPageUrl { get; set; }
    public DateTime? RevokedAt { get; set; }
    public bool IsActive { get; set; }
}

public sealed class FirefoxCaptureAdminStatusDto
{
    public bool Enabled { get; set; }
    public bool CatalogEnabled { get; set; }
    public int IndexerId { get; set; }
    public string IndexerName { get; set; } = string.Empty;
    public string CurrentExtensionVersion { get; set; } = string.Empty;
    public List<FirefoxCaptureDeviceDto> Devices { get; set; } = new();
}
