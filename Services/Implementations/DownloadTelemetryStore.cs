using System.Collections.Concurrent;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// Thread-safe in-memory store of the latest per-torrent telemetry snapshot per job. Registered as a
/// singleton so worker progress reports (HTTP requests) and the admin panel (Blazor circuit) share it.
/// Snapshots older than <see cref="StaleAfter"/> are pruned on write so a worker that dies mid-download
/// can't leave a stale entry lingering forever.
/// </summary>
public sealed class DownloadTelemetryStore : IDownloadTelemetryStore
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<int, Entry> _snapshots = new();

    private sealed record Entry(DateTime At, IReadOnlyList<DownloadTorrentTelemetry> Torrents);

    public void Update(int jobId, IReadOnlyList<DownloadTorrentTelemetry> torrents)
    {
        var now = DateTime.UtcNow;
        _snapshots[jobId] = new Entry(now, torrents);
        // Opportunistic prune — cheap, and keeps the map bounded without a background timer.
        foreach (var kvp in _snapshots)
            if (now - kvp.Value.At > StaleAfter)
                _snapshots.TryRemove(kvp.Key, out _);
    }

    public IReadOnlyList<DownloadTorrentTelemetry> Get(int jobId) =>
        _snapshots.TryGetValue(jobId, out var e) && DateTime.UtcNow - e.At <= StaleAfter
            ? e.Torrents
            : Array.Empty<DownloadTorrentTelemetry>();

    public void Remove(int jobId) => _snapshots.TryRemove(jobId, out _);
}
