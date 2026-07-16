using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

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

    public async Task<bool> ReportProgressAsync(int jobId, int progress, IReadOnlyList<DownloadTorrentTelemetry>? torrents, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync($"/api/fulfillment/{jobId}/progress",
            new ProgressRequest(progress, _worker.WorkerId, torrents?.ToList()), ct);
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
}
