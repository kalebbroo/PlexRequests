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
    /// <summary>Fetch cached TMDB episode titles for a season (used to name files in a season pack).</summary>
    Task<List<EpisodeDto>> GetSeasonEpisodesAsync(int tmdbId, int season, CancellationToken ct);
    /// <summary>Report aggregate 0-100 progress plus (optionally) the live per-torrent telemetry snapshot
    /// that drives the admin live-downloads panel.</summary>
    Task<bool> ReportProgressAsync(int jobId, int progress, IReadOnlyList<DownloadTorrentTelemetry>? torrents, CancellationToken ct);
    Task<bool> MarkFulfilledAsync(int requestId, CancellationToken ct);
    Task<bool> MarkFailedAsync(int requestId, string reason, CancellationToken ct);
    /// <summary>Report "no release findable yet" for a normal job: the web app parks it on a retry backoff
    /// (Deferred / request shows "Searching") instead of failing. Keyed by JOB id, not request id.</summary>
    Task<bool> MarkDeferredAsync(int jobId, string reason, CancellationToken ct);
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
}
