using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared.DTOs;

/// <summary>One cheap source observation. Detail-only fields may remain null until selective hydration.</summary>
public sealed class CatalogItemDto
{
    public string ExternalId { get; set; } = string.Empty;
    public string ReleaseName { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public string? InfoHash { get; set; }
    public string? MagnetUri { get; set; }
    public string? ImdbId { get; set; }
    public string? Category { get; set; }
    public string? Uploader { get; set; }
    public int? Seeders { get; set; }
    public int? Leechers { get; set; }
    public long? SizeBytes { get; set; }
    public DateTime? PublishedAt { get; set; }
    public MediaType? MediaType { get; set; }
    public bool NeedsHydration { get; set; }
}

/// <summary>
/// At-least-once ingestion envelope. BatchId makes replay harmless; Cursor advances only in the same
/// transaction that commits every item.
/// </summary>
public sealed class CatalogBatchDto
{
    public int IndexerId { get; set; }
    public string Source { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
    public string? Cursor { get; set; }
    /// <summary>False for live-search write-through, which must never alter an ingestion feed's cursor.</summary>
    public bool AdvanceCheckpoint { get; set; } = true;
    public DateTime ObservedAt { get; set; }
    public List<CatalogItemDto> Items { get; set; } = new();
}

public sealed class CatalogUpsertResultDto
{
    public bool DuplicateBatch { get; set; }
    public int ReleasesInserted { get; set; }
    public int ReleasesUpdated { get; set; }
    public int SightingsInserted { get; set; }
    public int SightingsUpdated { get; set; }
    public int UnresolvedSightings { get; set; }
}

public sealed class CatalogStatsDto
{
    public bool Enabled { get; set; }
    public bool UseForSearch { get; set; }
    public bool UseForMonitoring { get; set; }
    public long DatabaseSizeBytes { get; set; }
    public long Releases { get; set; }
    public long Sightings { get; set; }
    public long UnresolvedSightings { get; set; }
    public DateTime? OldestReleaseAt { get; set; }
    public DateTime? NewestReleaseAt { get; set; }
    public List<CatalogSourceStatsDto> Sources { get; set; } = new();
}

public sealed class CatalogSourceStatsDto
{
    public int IndexerId { get; set; }
    public string Source { get; set; } = string.Empty;
    public long Sightings { get; set; }
    public long TotalItemsSeen { get; set; }
    public long TotalItemsResolved { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string CircuitState { get; set; } = "Closed";
    public string? LastError { get; set; }
}

public sealed class CatalogCheckpointDto
{
    public int IndexerId { get; set; }
    public string? Cursor { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string CircuitState { get; set; } = "Closed";
    public string? LastError { get; set; }
}

public sealed class CatalogFailureDto
{
    public int IndexerId { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public IndexerBlockReason BlockReason { get; set; }
    public int? RetryAfterSeconds { get; set; }
    public DateTime OccurredAt { get; set; }
}

public sealed class CatalogQueryDto
{
    public string Title { get; set; } = string.Empty;
    public string? ImdbId { get; set; }
    public MediaType MediaType { get; set; }
    public int? Year { get; set; }
    public int Limit { get; set; } = 500;
}

/// <summary>Admin-facing catalog browser query. Blank search text returns the newest hydrated releases.</summary>
public sealed class CatalogBrowseQueryDto
{
    public string Search { get; set; } = string.Empty;
    public int? IndexerId { get; set; }
    public MediaType? MediaType { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public sealed class CatalogBrowsePageDto
{
    public long Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<CatalogBrowseItemDto> Items { get; set; } = new();
}

/// <summary>
/// Safe operational view of a hydrated release. It deliberately omits magnet URIs and infohashes: the
/// explorer is for proving coverage and parsing, while acquisition remains behind the fulfillment worker.
/// </summary>
public sealed class CatalogBrowseItemDto
{
    public string ReleaseName { get; set; } = string.Empty;
    public MediaType? MediaType { get; set; }
    public int? Year { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public bool IsSeasonPack { get; set; }
    public int Resolution { get; set; }
    public ReleaseSource ReleaseSource { get; set; }
    public string? Codec { get; set; }
    public long? SizeBytes { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public int IndexerId { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public string? Category { get; set; }
    public string? Uploader { get; set; }
    public int? Seeders { get; set; }
    public int? Leechers { get; set; }
    public int SightingCount { get; set; }
}
