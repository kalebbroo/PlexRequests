using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Catalog;

/// <summary>A canonical torrent release. InfoHash is the durable identity across mirrors/indexers.</summary>
public sealed class CatalogReleaseEntity
{
    public long Id { get; set; }
    [MaxLength(64)] public string InfoHash { get; set; } = string.Empty;
    [MaxLength(512)] public string ReleaseName { get; set; } = string.Empty;
    [MaxLength(512)] public string NormalizedTitle { get; set; } = string.Empty;
    [MaxLength(4096)] public string? MagnetUri { get; set; }
    [MaxLength(32)] public string? ImdbId { get; set; }
    public long? SizeBytes { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    // Parsed, rebuildable projections. ReleaseName remains the source of truth.
    public int ParserVersion { get; set; }
    public MediaType? MediaType { get; set; }
    public int? Year { get; set; }
    public int? Season { get; set; }
    public int? SeasonEnd { get; set; }
    public int? Episode { get; set; }
    public int? EpisodeStart { get; set; }
    public int? EpisodeEnd { get; set; }
    public bool IsSeasonPack { get; set; }
    public bool IsCompleteSeries { get; set; }
    public int Resolution { get; set; }
    public ReleaseSource ReleaseSource { get; set; }
    [MaxLength(32)] public string? Codec { get; set; }

    public ICollection<CatalogSightingEntity> Sightings { get; set; } = new List<CatalogSightingEntity>();
}

/// <summary>
/// One source's observation of a release. ReleaseId may be null for a cheap listing row whose detail page
/// has not been hydrated to an infohash yet.
/// </summary>
public sealed class CatalogSightingEntity
{
    public long Id { get; set; }
    public long? ReleaseId { get; set; }
    public CatalogReleaseEntity? Release { get; set; }
    public int IndexerId { get; set; }
    [MaxLength(128)] public string Source { get; set; } = string.Empty;
    [MaxLength(512)] public string ExternalId { get; set; } = string.Empty;
    [MaxLength(2048)] public string? SourceUrl { get; set; }
    [MaxLength(512)] public string ReleaseName { get; set; } = string.Empty;
    [MaxLength(128)] public string? Category { get; set; }
    [MaxLength(128)] public string? Uploader { get; set; }
    public int? Seeders { get; set; }
    public int? Leechers { get; set; }
    public long? SizeBytes { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? LastHydratedAt { get; set; }
    public CatalogHydrationState HydrationState { get; set; }
    [MaxLength(512)] public string? HydrationError { get; set; }
}

/// <summary>Durable progress and operation-specific health for one ingestion source.</summary>
public sealed class CatalogCheckpointEntity
{
    public int Id { get; set; }
    public int IndexerId { get; set; }
    [MaxLength(128)] public string Source { get; set; } = string.Empty;
    [MaxLength(2048)] public string? Cursor { get; set; }
    [MaxLength(128)] public string? LastBatchId { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public CatalogCircuitState CircuitState { get; set; }
    [MaxLength(512)] public string? LastError { get; set; }
    public long TotalItemsSeen { get; set; }
    public long TotalItemsResolved { get; set; }
}

/// <summary>Small idempotency ledger preventing a retried old batch from moving an opaque cursor backwards.</summary>
public sealed class CatalogBatchReceiptEntity
{
    public long Id { get; set; }
    public int IndexerId { get; set; }
    [MaxLength(128)] public string BatchId { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public DateTime CommittedAt { get; set; }
}

public enum CatalogHydrationState
{
    NotRequired,
    Pending,
    Hydrated,
    Failed
}

public enum CatalogCircuitState
{
    Closed,
    Open,
    HalfOpen,
    Disabled
}
