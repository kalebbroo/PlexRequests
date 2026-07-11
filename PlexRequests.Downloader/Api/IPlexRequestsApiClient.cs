using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Api;

/// <summary>Client for the PlexRequests web app fulfillment API (authenticated by shared secret).</summary>
public interface IPlexRequestsApiClient
{
    Task<IReadOnlyList<FulfillmentJobDto>> ClaimAsync(int max, CancellationToken ct);
    Task<bool> ReportProgressAsync(int jobId, int progress, CancellationToken ct);
    Task<bool> MarkFulfilledAsync(int requestId, CancellationToken ct);
    Task<bool> MarkFailedAsync(int requestId, string reason, CancellationToken ct);
}
