using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequests.Downloader.Api;

/// <summary>
/// Typed HttpClient over the web app's fulfillment endpoints. The base address and the
/// X-Fulfillment-Key header are configured on the HttpClient at registration time.
/// Callbacks return a bool (success) so the worker can retry on transient failures.
/// </summary>
public class PlexRequestsApiClient(HttpClient http, IOptions<WorkerOptions> worker, ILogger<PlexRequestsApiClient> logger)
    : IPlexRequestsApiClient
{
    private readonly HttpClient _http = http;
    private readonly WorkerOptions _worker = worker.Value;
    private readonly ILogger<PlexRequestsApiClient> _logger = logger;

    // Debounce Plex library refreshes across all jobs in this process — a 30-episode season-pack
    // fan-out would otherwise trigger one refresh call per file.
    private static readonly ConcurrentDictionary<MediaType, DateTime> _lastRefresh = new();
    private static readonly TimeSpan RefreshDebounce = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<FulfillmentJobDto>> ClaimAsync(int max, CancellationToken ct)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/fulfillment/claim",
                new ClaimRequest(_worker.WorkerId, max), ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Claim returned {Status}", (int)resp.StatusCode);
                return Array.Empty<FulfillmentJobDto>();
            }
            var jobs = await resp.Content.ReadFromJsonAsync<List<FulfillmentJobDto>>(cancellationToken: ct);
            return jobs ?? new List<FulfillmentJobDto>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Claim request failed (web app unreachable?)");
            return Array.Empty<FulfillmentJobDto>();
        }
    }

    public async Task<DownloadPreferencesDto?> GetConfigAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync("/api/fulfillment/config", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Config fetch returned {Status}", (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<DownloadPreferencesDto>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Config request failed (web app unreachable?)");
            return null;
        }
    }

    public async Task<bool> ReportProgressAsync(int jobId, int progress, IReadOnlyList<DownloadTransferTelemetry>? transfers, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync($"/api/fulfillment/{jobId}/progress",
            new ProgressRequest(progress, _worker.WorkerId, transfers?.ToList()), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> MarkFulfilledAsync(int requestId, CancellationToken ct)
    {
        var resp = await _http.PostAsync($"/api/requests/{requestId}/fulfilled", content: null, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> MarkFailedAsync(int requestId, string reason, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync($"/api/requests/{requestId}/failed", new FailRequest(reason), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> MarkPartiallyCompletedAsync(int requestId, string reason, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync($"/api/requests/{requestId}/partially-completed", new FailRequest(reason), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> MarkDeferredAsync(int jobId, string reason, bool candidatesRejected, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync($"/api/fulfillment/{jobId}/deferred", new FailRequest(reason, candidatesRejected), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> MarkUpgradeExhaustedAsync(int jobId, CancellationToken ct)
    {
        var resp = await _http.PostAsync($"/api/fulfillment/{jobId}/upgrade-exhausted", content: null, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> MarkUpgradedAsync(int jobId, CancellationToken ct)
    {
        var resp = await _http.PostAsync($"/api/fulfillment/{jobId}/upgraded", content: null, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<LibraryOrganizationPreferencesDto?> GetLibraryConfigAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync("/api/fulfillment/library-config", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Library config fetch returned {Status}", (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<LibraryOrganizationPreferencesDto>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Library config request failed (web app unreachable?)");
            return null;
        }
    }

    public async Task<IReadOnlyList<NetworkShareMountDto>?> GetNetworkSharesAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync("/api/fulfillment/network-shares", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Network shares fetch returned {Status}", (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<List<NetworkShareMountDto>>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Network shares request failed (web app unreachable?)");
            return null;
        }
    }

    public async Task<List<IndexerConfigDto>?> GetIndexersAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync("/api/fulfillment/indexers", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Indexer config fetch returned {Status}", (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<List<IndexerConfigDto>>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Indexer config request failed (web app unreachable?)");
            return null;
        }
    }

    public async Task<List<WantedEpisodeDto>> GetWantedAsync(CancellationToken ct)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<WantedEpisodeDto>>("/api/fulfillment/wanted", ct) ?? new();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Wanted-episode fetch failed");
            return new();
        }
    }

    public async Task<CatalogCheckpointDto?> GetCatalogCheckpointAsync(int indexerId, CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync($"/api/fulfillment/catalog/checkpoints/{indexerId}", ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<CatalogCheckpointDto>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Catalog checkpoint {IndexerId} unavailable", indexerId);
            return null;
        }
    }

    public async Task<CatalogUpsertResultDto?> PushCatalogBatchAsync(CatalogBatchDto batch, CancellationToken ct)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/api/fulfillment/catalog/batches", batch, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<CatalogUpsertResultDto>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not commit catalog batch {BatchId}", batch.BatchId);
            return null;
        }
    }

    public async Task<CatalogCheckpointDto?> ReportCatalogFailureAsync(CatalogFailureDto failure, CancellationToken ct)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/api/fulfillment/catalog/failures", failure, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<CatalogCheckpointDto>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not report catalog failure for {Source}", failure.Source);
            return null;
        }
    }

    public async Task<IReadOnlyList<ReleaseCandidate>?> SearchCatalogAsync(FulfillmentJobDto job, CancellationToken ct)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/api/fulfillment/catalog/search", new CatalogQueryDto
            {
                Title = job.MediaType == MediaType.Music && job.RequestScope == RequestScopeKind.ArtistCatalog
                    ? job.Music?.Artist ?? job.Title
                    : AcquisitionQuery.Build(job, includeMovieYear: false),
                ImdbId = job.ImdbId,
                MediaType = job.MediaType,
                // MusicBrainz carries an original release-group year while torrents often carry a reissue
                // or remaster year. Identity ranking handles that safely; a hard catalog filter would hide it.
                Year = job.MediaType == MediaType.Music ? null : job.Year,
                Limit = 500
            }, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<List<ReleaseCandidate>>(cancellationToken: ct) ?? new();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Catalog search unavailable for {Title}", job.Title);
            return null;
        }
    }

    public async Task<int> ReportRssGrabsAsync(IReadOnlyList<RssGrabDto> grabs, CancellationToken ct)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/fulfillment/rss-grab", grabs, ct);
            if (!resp.IsSuccessStatusCode) return 0;
            return await resp.Content.ReadFromJsonAsync<int>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not report RSS grabs");
            return 0;
        }
    }

    public async Task<int> PushRecommendedAsync(IReadOnlyList<RecommendedFeedItemDto> items, CancellationToken ct)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/fulfillment/recommended", items, ct);
            if (!resp.IsSuccessStatusCode) return 0;
            return await resp.Content.ReadFromJsonAsync<int>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Recommended feed push failed");
            return 0;
        }
    }

    public async Task<SearchTaskDto?> ClaimSearchTaskAsync(string workerId, CancellationToken ct)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/fulfillment/search/claim", new ClaimRequest(workerId, 1), ct);
            // NoContent is the normal "nothing queued" answer and must not be logged as a problem.
            if (resp.StatusCode == System.Net.HttpStatusCode.NoContent || !resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<SearchTaskDto>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Search-task claim failed");
            return null;
        }
    }

    public async Task<bool> CompleteSearchTaskAsync(int taskId, SearchTaskResultDto result, CancellationToken ct)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"/api/fulfillment/search/{taskId}/results", result, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not report interactive search results for task {TaskId}", taskId);
            return false;
        }
    }

    public async Task<bool> RegisterTransfersAsync(int jobId, IReadOnlyList<TrackedTransferDto> transfers, CancellationToken ct)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"/api/fulfillment/{jobId}/transfers", transfers, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not best-effort in spirit — losing this loses the durable link — but the reconciler recovers
            // on the next pass once the web app is reachable again, so a failure here is a delay, not a leak.
            _logger.LogWarning(ex, "Could not register transfers for job {JobId}", jobId);
            return false;
        }
    }

    public async Task<List<TrackedTransferDto>> GetActiveTransfersAsync(CancellationToken ct)
    {
        try
        {
            var list = await _http.GetFromJsonAsync<List<TrackedTransferDto>>("/api/fulfillment/transfers/active", ct);
            return list ?? new();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An empty list means "do nothing this pass", which is the correct response to not knowing.
            _logger.LogDebug(ex, "Active torrent list unavailable");
            return new();
        }
    }

    public async Task<FulfillmentJobDto?> GetJobAsync(int jobId, CancellationToken ct)
    {
        try { return await _http.GetFromJsonAsync<FulfillmentJobDto>($"/api/fulfillment/jobs/{jobId}", ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Job {JobId} unavailable", jobId);
            return null;
        }
    }

    public async Task<bool> ReportTransferStateAsync(IReadOnlyList<TransferStateUpdateDto> updates, CancellationToken ct)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/fulfillment/transfers/state", updates, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not report transfer state");
            return false;
        }
    }

    public async Task<bool> BlocklistAsync(int jobId, BlocklistRequestDto request, CancellationToken ct)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"/api/fulfillment/{jobId}/blocklist", request, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: failing to record a blocklist entry must not change the job's outcome.
            _logger.LogWarning(ex, "Could not blocklist a failed release for job {JobId}", jobId);
            return false;
        }
    }

    public async Task<int> ImportLegacyTorznabAsync(IReadOnlyList<LegacyTorznabEndpointDto> endpoints, CancellationToken ct)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/fulfillment/indexers/import-legacy", endpoints, ct);
            if (!resp.IsSuccessStatusCode) return 0;
            return await resp.Content.ReadFromJsonAsync<int>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not import legacy Torznab endpoints; will retry on the next start");
            return 0;
        }
    }

    public async Task<bool> ReportIndexerStatusAsync(IReadOnlyList<IndexerStatusReportDto> reports, CancellationToken ct)
    {
        if (reports.Count == 0) return true;
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/fulfillment/indexer-status", reports, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Indexer status report failed (web app unreachable?)");
            return false;
        }
    }

    public async Task<List<EpisodeDto>> GetSeasonEpisodesAsync(int tmdbId, int season, CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync($"/api/fulfillment/episodes?tmdbId={tmdbId}&season={season}", ct);
            if (!resp.IsSuccessStatusCode) return new List<EpisodeDto>();
            return await resp.Content.ReadFromJsonAsync<List<EpisodeDto>>(cancellationToken: ct) ?? new List<EpisodeDto>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Episode title fetch failed for tmdbId={TmdbId} season={Season}", tmdbId, season);
            return new List<EpisodeDto>();
        }
    }

    public async Task<bool> ReportImportedFilesAsync(int jobId, IReadOnlyList<ImportedFileDto> files, CancellationToken ct)
    {
        if (files.Count == 0) return true;
        try
        {
            var resp = await _http.PostAsJsonAsync($"/api/fulfillment/{jobId}/imported-files", files, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not persist imported-files audit rows for job {JobId}", jobId);
            return false;
        }
    }

    public async Task<bool> RefreshLibraryAsync(MediaType mediaType, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_lastRefresh.TryGetValue(mediaType, out var last) && now - last < RefreshDebounce)
            return true; // recently refreshed for this media type; skip
        _lastRefresh[mediaType] = now;

        try
        {
            var resp = await _http.PostAsJsonAsync("/api/fulfillment/refresh-library", new RefreshLibraryRequest(mediaType), ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Plex library refresh trigger failed for {MediaType}", mediaType);
            return false;
        }
    }

    public async Task<PlexVerificationResult> VerifyLibraryAsync(FulfillmentJobDto job, CancellationToken ct)
    {
        try
        {
            var request = new PlexVerificationRequest(
                job.MediaType,
                job.Media?.Kind ?? job.Music?.Kind ?? job.MediaType.DefaultKind(),
                job.Media?.Provider,
                job.Media?.Id,
                job.Music?.Artist,
                job.Music?.Album,
                job.Music?.Track,
                job.Music?.ExpectedAlbums,
                job.Music?.Tracks.Select(x => x.Title).ToList(),
                job.Music?.Tracks.FirstOrDefault()?.DurationMs);
            var response = await _http.PostAsJsonAsync("/api/fulfillment/verify-library", request, ct);
            if (!response.IsSuccessStatusCode)
                return new(false, Detail: $"Verification API returned {(int)response.StatusCode}");
            return await response.Content.ReadFromJsonAsync<PlexVerificationResult>(cancellationToken: ct)
                   ?? new(false, Detail: "Verification API returned an empty response");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Library verification unavailable for job {JobId}", job.Id);
            return new(false, Detail: "Verification API is temporarily unavailable");
        }
    }
}
