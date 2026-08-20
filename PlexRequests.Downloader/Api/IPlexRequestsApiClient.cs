using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Api;

/// <summary>Client for the PlexRequests web app fulfillment API (authenticated by shared secret).</summary>
public interface IPlexRequestsApiClient
{
    Task<IReadOnlyList<FulfillmentJobDto>> ClaimAsync(int max, CancellationToken ct);
    /// <summary>Fetch the admin-configured global download preferences, or null if unreachable.</summary>
    Task<DownloadPreferencesDto?> GetConfigAsync(CancellationToken ct);
    /// <summary>Fetch the admin-configured library organization settings, or null if unreachable.</summary>
    Task<LibraryOrganizationPreferencesDto?> GetLibraryConfigAsync(CancellationToken ct);
    /// <summary>Fetch admin-configured network shares (with credentials) to mount, or null if unreachable.</summary>
    Task<IReadOnlyList<NetworkShareMountDto>?> GetNetworkSharesAsync(CancellationToken ct);
    /// <summary>Fetch the admin per-indexer enable/priority config, or null if unreachable.</summary>
    Task<List<IndexerConfigDto>?> GetIndexersAsync(CancellationToken ct);
    /// <summary>Report per-indexer outcomes for one search pass (drives the admin Indexers panel's health).</summary>
    Task<bool> ReportIndexerStatusAsync(IReadOnlyList<IndexerStatusReportDto> reports, CancellationToken ct);

    Task<CatalogCheckpointDto?> GetCatalogCheckpointAsync(int indexerId, CancellationToken ct);
    Task<CatalogUpsertResultDto?> PushCatalogBatchAsync(CatalogBatchDto batch, CancellationToken ct);
    Task<CatalogCheckpointDto?> ReportCatalogFailureAsync(CatalogFailureDto failure, CancellationToken ct);
    Task<IReadOnlyList<PlexRequestsHosted.Shared.Releases.ReleaseCandidate>?> SearchCatalogAsync(
        FulfillmentJobDto job,
        CancellationToken ct);

    /// <summary>
    /// One-time push of Torznab endpoints found in this container's own environment, so the admin doesn't
    /// have to re-enter a URL and API key that were already configured. The web app can't read them itself —
    /// Indexer__Torznab__* is set on the downloader. Idempotent server-side by URL.
    /// </summary>
    Task<int> ImportLegacyTorznabAsync(IReadOnlyList<LegacyTorznabEndpointDto> endpoints, CancellationToken ct);

    /// <summary>Claim one queued admin-initiated search, or null when there's nothing waiting.</summary>
    Task<SearchTaskDto?> ClaimSearchTaskAsync(string workerId, CancellationToken ct);

    /// <summary>Return everything a search found, including what was rejected and why.</summary>
    Task<bool> CompleteSearchTaskAsync(int taskId, SearchTaskResultDto result, CancellationToken ct);

    /// <summary>Record a release that failed, so it is never grabbed for this request again.</summary>
    Task<bool> BlocklistAsync(int jobId, BlocklistRequestDto request, CancellationToken ct);

    // ---- Torrent reconciliation ----------------------------------------------------------------
    /// <summary>Record the torrents backing a job, so the link survives this process.</summary>
    Task<bool> RegisterTransfersAsync(int jobId, IReadOnlyList<TrackedTransferDto> transfers, CancellationToken ct);
    /// <summary>Everything the database still believes is in flight — the reconciler's work list.</summary>
    Task<List<TrackedTransferDto>> GetActiveTransfersAsync(CancellationToken ct);
    /// <summary>One job's context, so a torrent can be imported by whoever finds it finished — not only by
    /// the process that added it.</summary>
    Task<FulfillmentJobDto?> GetJobAsync(int jobId, CancellationToken ct);
    Task<bool> ReportTransferStateAsync(IReadOnlyList<TransferStateUpdateDto> updates, CancellationToken ct);

    /// <summary>Push the titles currently trending on an indexer. Returns how many resolved to metadata.</summary>
    Task<int> PushRecommendedAsync(IReadOnlyList<RecommendedFeedItemDto> items, CancellationToken ct);

    /// <summary>Episodes automatic release monitoring should be watching for.</summary>
    Task<List<WantedEpisodeDto>> GetWantedAsync(CancellationToken ct);

    /// <summary>Report releases the sweep matched. Returns how many were queued.</summary>
    Task<int> ReportRssGrabsAsync(IReadOnlyList<RssGrabDto> grabs, CancellationToken ct);
    /// <summary>Fetch cached TMDB episode titles for a season (used to name files in a season pack).</summary>
    Task<List<EpisodeDto>> GetSeasonEpisodesAsync(int tmdbId, int season, CancellationToken ct);
    /// <summary>Report aggregate 0-100 progress plus (optionally) the live per-torrent telemetry snapshot
    /// that drives the admin live-downloads panel.</summary>
    Task<bool> ReportProgressAsync(int jobId, int progress, IReadOnlyList<DownloadTransferTelemetry>? transfers, CancellationToken ct);
    Task<bool> MarkFulfilledAsync(int requestId, CancellationToken ct);
    Task<bool> MarkFailedAsync(int requestId, string reason, CancellationToken ct);
    /// <summary>Report "no release findable yet" for a normal job: the web app parks it on a retry backoff
    /// (Deferred / request shows "Searching") instead of failing. Keyed by JOB id, not request id.</summary>
    Task<bool> MarkDeferredAsync(int jobId, string reason, bool candidatesRejected, CancellationToken ct);
    /// <summary>Report that an upgrade job found nothing better than what's already imported (terminal, non-failure).</summary>
    Task<bool> MarkUpgradeExhaustedAsync(int jobId, CancellationToken ct);
    /// <summary>Report a successful quality upgrade: the better release imported and old files were deleted on
    /// disk. The web app drops the superseded audit rows, recomputes achieved quality, and notifies.</summary>
    Task<bool> MarkUpgradedAsync(int jobId, CancellationToken ct);
    /// <summary>Some torrents in the job imported before another failed — distinct from a hard failure.</summary>
    Task<bool> MarkPartiallyCompletedAsync(int requestId, string reason, CancellationToken ct);
    /// <summary>Persist the durable audit trail of what got imported for a job.</summary>
    Task<bool> ReportImportedFilesAsync(int jobId, IReadOnlyList<ImportedFileDto> files, CancellationToken ct);
    /// <summary>Ask Plex to rescan the library section for this media type. Best-effort; client-side
    /// debounced so a large season-pack fan-out doesn't hammer Plex with one refresh per file.</summary>
    Task<bool> RefreshLibraryAsync(MediaType mediaType, CancellationToken ct);
    /// <summary>Ask the web app to verify that Plex has actually indexed a completed import.</summary>
    Task<PlexVerificationResult> VerifyLibraryAsync(FulfillmentJobDto job, CancellationToken ct);
}
