using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// Closes the gap between observing a hard upstream block and receiving the persisted circuit state on the
/// downloader's next configuration refresh. Without this process-local guard, every search started during
/// that cache window repeats the same request that just received a 403.
/// </summary>
public sealed class IndexerRuntimeCircuit
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, OpenCircuit> _open = new();

    public bool Allows(int indexerId, DateTime now)
    {
        if (indexerId <= 0) return true;
        lock (_gate)
        {
            if (!_open.TryGetValue(indexerId, out var circuit)) return true;
            if (circuit.OpenUntil > now) return false;
            _open.Remove(indexerId);
            return true;
        }
    }

    public void Record(IEnumerable<IndexerStatusReportDto> reports)
    {
        lock (_gate)
        {
            foreach (var report in reports.Where(x => x.IndexerId > 0))
            {
                var observedAt = report.SearchedAt == default ? DateTime.UtcNow : report.SearchedAt;
                if (_open.TryGetValue(report.IndexerId, out var existing)
                    && existing.ObservedAt >= observedAt)
                    continue;

                if (report.Success)
                {
                    _open.Remove(report.IndexerId);
                    continue;
                }

                // Ordinary transport failures keep using the durable consecutive-failure policy. Typed
                // refusals are safe to suppress immediately because retrying them inside the cache window
                // cannot make the upstream challenge, ban, or rate limit disappear.
                if (report.BlockReason == IndexerBlockReason.None) continue;
                _open[report.IndexerId] = new OpenCircuit(
                    observedAt,
                    observedAt + IndexerCircuitPolicy.FailureDelay(report.BlockReason, consecutiveFailures: 1));
            }
        }
    }

    private sealed record OpenCircuit(DateTime ObservedAt, DateTime OpenUntil);
}
