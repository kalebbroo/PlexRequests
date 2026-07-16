using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Abstractions;

/// <summary>
/// Holds the latest live per-torrent download telemetry pushed by the downloader worker, keyed by
/// fulfillment job id. Deliberately in-memory and ephemeral: this data changes every few seconds and is
/// only meaningful while a download is in flight, so persisting it would be pure churn. On a web-app
/// restart the snapshots are simply re-populated on the worker's next progress report.
/// </summary>
public interface IDownloadTelemetryStore
{
    /// <summary>Replace the telemetry snapshot for a job (called from the worker progress endpoint).</summary>
    void Update(int jobId, IReadOnlyList<DownloadTorrentTelemetry> torrents);
    /// <summary>Latest snapshot for a job, or an empty list if none is held.</summary>
    IReadOnlyList<DownloadTorrentTelemetry> Get(int jobId);
    /// <summary>Drop a job's snapshot (e.g. once it reaches a terminal state).</summary>
    void Remove(int jobId);
}

/// <summary>
/// Read model for the admin "Downloads" live panel: the in-flight fulfillment jobs (with live per-torrent
/// telemetry) plus a short tail of recently finished/failed jobs, so an admin can watch the whole
/// approved → downloading → imported → available lifecycle at a glance.
/// </summary>
public interface IDownloadMonitorService
{
    /// <summary>Active jobs (queued/claimed/downloading) plus jobs that reached a terminal state within
    /// <paramref name="recentMinutes"/>, newest activity first.</summary>
    Task<List<DownloadJobView>> GetActiveAndRecentAsync(int recentMinutes = 30);
}
