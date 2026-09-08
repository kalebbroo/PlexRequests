using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>Process-local copy of the downloader heartbeat. The worker republishes every minute, so a web
/// restart heals quickly without adding high-churn storage samples to the application database.</summary>
public sealed class StorageTelemetryStore : IStorageTelemetryStore
{
    private readonly object _gate = new();
    private StorageStatusDto? _latest;
    private DateTime _lastAlertAt;
    private string? _lastAlertKey;

    public bool Update(StorageStatusDto status)
    {
        lock (_gate)
        {
            _latest = status;
            if (status.State is not (StorageHealthState.Warning or StorageHealthState.Paused or StorageHealthState.Offline))
            {
                // A recovery closes the incident. The same volume crossing the threshold again is a new
                // incident and should notify opted-in admins even if it happens inside the six-hour repeat window.
                _lastAlertKey = null;
                return false;
            }

            var key = $"{status.State}:{status.BlockingJobId}:{status.Message}";
            var shouldAlert = !string.Equals(key, _lastAlertKey, StringComparison.Ordinal)
                || DateTime.UtcNow - _lastAlertAt >= TimeSpan.FromHours(6);
            if (shouldAlert)
            {
                _lastAlertKey = key;
                _lastAlertAt = DateTime.UtcNow;
            }
            return shouldAlert;
        }
    }

    public StorageStatusDto? Get()
    {
        lock (_gate) return _latest;
    }
}
