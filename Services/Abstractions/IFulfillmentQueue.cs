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

    /// <summary>Close the job as partially completed: some torrents imported before another failed.
    /// A later retry (re-enqueue) only re-fetches what's still missing.</summary>
    Task MarkPartiallyCompletedAsync(int mediaRequestId, string reason);

    /// <summary>Park a job whose release isn't findable yet: bump its defer count, set the retry backoff, and
    /// flip it to <see cref="Shared.Enums.FulfillmentStatus.Deferred"/> instead of failing. The scheduler
    /// re-queues it once the backoff elapses. Returns details the caller uses to update the request/notify.</summary>
    Task<DeferResult> MarkDeferredAsync(int jobId, string reason);

    /// <summary>An upgrade search found nothing better: close the upgrade job (terminal, non-failure) and
    /// stamp the request's upgrade cooldown so it's re-considered later, not immediately. No-op if not found.</summary>
    Task MarkUpgradeExhaustedAsync(int jobId);

    /// <summary>Enqueue an automatic quality-upgrade job for an already-available request that was downloaded
    /// below its preferred quality. <paramref name="replacePaths"/> are the existing library files this
    /// upgrade supersedes; <paramref name="episodes"/> scopes a TV upgrade to just the below-target episodes
    /// (empty ⇒ movie/whole-title). No-op (returns false) if an upgrade job for the request is already active.</summary>
    Task<bool> EnqueueUpgradeAsync(MediaRequestDto request, Quality target, IReadOnlyList<string> replacePaths, IReadOnlyList<(int season, int episode)> episodes);

    /// <summary>Recompute a request's <c>AchievedQuality</c>/<c>CutoffMet</c> from its imported files (min
    /// video resolution vs. the resolved target) and persist them. Called after a download or upgrade imports.</summary>
    Task<Quality> RecomputeAchievedQualityAsync(int mediaRequestId);
}

/// <summary>Outcome of parking a job as Deferred, so the endpoint can update the request and decide whether
/// to escalate to admins.</summary>
/// <param name="Found">False if no job with that id exists.</param>
/// <param name="IsUpgrade">Whether the parked job was an upgrade job (the caller handles those differently).</param>
/// <param name="DeferCount">The job's new empty-search count.</param>
/// <param name="NextRetryAt">When the job becomes claimable again.</param>
/// <param name="ShouldEscalate">True exactly once, when this deferral crosses the escalation threshold.</param>
public record DeferResult(bool Found, bool IsUpgrade, int DeferCount, DateTime? NextRetryAt, bool ShouldEscalate);
