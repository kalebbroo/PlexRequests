using System.Collections.Concurrent;
using PlexRequests.Downloader.Organize;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Import;

/// <summary>
/// Serializes imports by protocol plus backend transfer id and shares a successful result with every caller.
/// Both the durable reconciler and the per-job monitor can observe the same finished transfer; without
/// this boundary they independently moved the same source into the same Plex path.
/// </summary>
public interface ITransferImportCoordinator
{
    Task<ImportResult> RunOnceAsync(
        AcquisitionProtocol protocol,
        string transferId,
        Func<CancellationToken, Task<ImportResult>> import,
        CancellationToken ct);
}

public sealed class TransferImportCoordinator : ITransferImportCoordinator
{
    private static readonly TimeSpan SuccessRetention = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Entry> _imports = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ImportResult> RunOnceAsync(
        AcquisitionProtocol protocol,
        string transferId,
        Func<CancellationToken, Task<ImportResult>> import,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
        ArgumentNullException.ThrowIfNull(import);
        PruneExpiredSuccesses();

        var candidate = new Entry(new Lazy<Task<ImportResult>>(
            () => import(ct), LazyThreadSafetyMode.ExecutionAndPublication));
        var key = $"{(int)protocol}:{transferId}";
        var entry = _imports.GetOrAdd(key, candidate);

        try
        {
            var result = await entry.Work.Value.WaitAsync(ct);
            if (result.Success)
            {
                entry.MarkCompleted();
            }
            else
            {
                // A genuine failure is retryable by a later pass; only success is shared/cached.
                _imports.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            }
            return result;
        }
        catch
        {
            // Do not poison this transfer id forever because one attempt faulted or was cancelled.
            if (entry.Work.IsValueCreated && entry.Work.Value.IsCompleted)
                _imports.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            throw;
        }
    }

    private void PruneExpiredSuccesses()
    {
        var cutoff = DateTime.UtcNow - SuccessRetention;
        foreach (var pair in _imports)
        {
            var completedAt = pair.Value.CompletedAt;
            if (completedAt is not null && completedAt < cutoff)
                _imports.TryRemove(pair);
        }
    }

    private sealed class Entry(Lazy<Task<ImportResult>> work)
    {
        private long _completedAtTicks;
        public Lazy<Task<ImportResult>> Work { get; } = work;
        public DateTime? CompletedAt
        {
            get
            {
                var ticks = Interlocked.Read(ref _completedAtTicks);
                return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        public void MarkCompleted() =>
            Interlocked.CompareExchange(ref _completedAtTicks, DateTime.UtcNow.Ticks, 0);
    }
}
