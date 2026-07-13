using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Abstractions;

/// <summary>
/// Durable queue of download jobs for the out-of-process fulfillment worker. The web app enqueues
/// on approval; the worker claims jobs and reports terminal state through the secured HTTP API.
/// The default implementation is a table in the app database (no external broker dependency).
/// </summary>
public interface IFulfillmentQueue
{
    /// <summary>Enqueue a job for an approved request (no-op if one is already active for it).</summary>
    /// <summary>Queue a request for download. When <paramref name="force"/> is true, the on-Plex dedup
    /// is bypassed (re-fetch even if the content is already present) — used by Report-a-Problem re-downloads.</summary>
    Task EnqueueAsync(MediaRequestDto request, bool force = false);

    /// <summary>Atomically claim up to <paramref name="max"/> queued jobs for a worker.</summary>
    Task<List<FulfillmentJobDto>> ClaimNextAsync(string workerId, int max = 1);

    /// <summary>Record download progress (0-100) for a claimed job.</summary>
    Task<bool> ReportProgressAsync(int jobId, int progress);

    /// <summary>Close the job for a request as completed.</summary>
    Task MarkCompletedAsync(int mediaRequestId);

    /// <summary>Close the job for a request as failed, recording the reason.</summary>
    Task MarkFailedAsync(int mediaRequestId, string reason);
}
