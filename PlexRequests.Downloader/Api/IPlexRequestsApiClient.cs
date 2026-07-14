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
    /// <summary>Fetch cached TMDB episode titles for a season (used to name files in a season pack).</summary>
    Task<List<EpisodeDto>> GetSeasonEpisodesAsync(int tmdbId, int season, CancellationToken ct);
    Task<bool> ReportProgressAsync(int jobId, int progress, CancellationToken ct);
    Task<bool> MarkFulfilledAsync(int requestId, CancellationToken ct);
    Task<bool> MarkFailedAsync(int requestId, string reason, CancellationToken ct);
    /// <summary>Some torrents in the job imported before another failed — distinct from a hard failure.</summary>
    Task<bool> MarkPartiallyCompletedAsync(int requestId, string reason, CancellationToken ct);
    /// <summary>Persist the durable audit trail of what got imported for a job.</summary>
    Task<bool> ReportImportedFilesAsync(int jobId, IReadOnlyList<ImportedFileDto> files, CancellationToken ct);
    /// <summary>Ask Plex to rescan the library section for this media type. Best-effort; client-side
    /// debounced so a large season-pack fan-out doesn't hammer Plex with one refresh per file.</summary>
    Task<bool> RefreshLibraryAsync(MediaType mediaType, CancellationToken ct);
}
