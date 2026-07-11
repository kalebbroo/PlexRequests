using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;

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

    public async Task<bool> ReportProgressAsync(int jobId, int progress, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync($"/api/fulfillment/{jobId}/progress",
            new ProgressRequest(progress, _worker.WorkerId), ct);
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
}
