using System.Collections.Concurrent;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Download;
using PlexRequests.Downloader.Organize;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Worker;

/// <summary>
/// Removes a completed transfer from its backend after its library destinations have been verified.
/// The backend owns payload deletion, so cleanup stays confined to the transfer it originally created
/// instead of recursively deleting an importer-supplied path.
/// </summary>
public interface IPostImportCleanup
{
    Task<bool> RunAsync(AcquisitionProtocol protocol, string transferId, ImportResult result, CancellationToken ct);
}

public sealed class PostImportCleanup(
    IAcquisitionBackendRegistry acquisitionBackends,
    ILibraryOrganizationProvider libraryPreferences,
    ILogger<PostImportCleanup> logger) : IPostImportCleanup
{
    // The durable reconciler and the per-job monitor can observe the same completed transfer. The import
    // itself is already serialized by TransferImportCoordinator, but both callers receive its successful
    // result and used to race into backend removal. In production that produced one successful Deluge
    // removal immediately followed by InvalidTorrentError from the losing caller. Share cleanup success
    // across the same boundary so destructive work also happens exactly once.
    private static readonly TimeSpan SuccessRetention = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, CleanupEntry> _cleanups =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> RunAsync(
        AcquisitionProtocol protocol, string transferId, ImportResult result, CancellationToken ct)
    {
        if (!result.Success || result.MediaFileCount == 0 || result.Files.Count == 0)
        {
            logger.LogWarning("Retaining transfer {TransferId}: import did not produce verified media files", transferId);
            return false;
        }

        if (!DestinationsMatch(result.Files, out var reason))
        {
            logger.LogWarning("Retaining transfer {TransferId}: {Reason}", transferId, reason);
            return false;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
        PruneExpiredSuccesses();

        var key = $"{(int)protocol}:{transferId}";
        var candidate = new CleanupEntry(new Lazy<Task<bool>>(
            () => RemoveFromBackendAsync(protocol, transferId, ct),
            LazyThreadSafetyMode.ExecutionAndPublication));
        var entry = _cleanups.GetOrAdd(key, candidate);

        try
        {
            var removed = await entry.Work.Value.WaitAsync(ct);
            if (removed)
            {
                entry.MarkCompleted();
            }
            else
            {
                // A declined or failed cleanup must remain retryable on a later reconciliation pass.
                _cleanups.TryRemove(new KeyValuePair<string, CleanupEntry>(key, entry));
            }
            return removed;
        }
        catch
        {
            // A caller cancelling its own wait must not evict cleanup that another caller is still running.
            if (entry.Work.IsValueCreated && entry.Work.Value.IsCompleted)
                _cleanups.TryRemove(new KeyValuePair<string, CleanupEntry>(key, entry));
            throw;
        }
    }

    private async Task<bool> RemoveFromBackendAsync(
        AcquisitionProtocol protocol, string transferId, CancellationToken ct)
    {
        if (!acquisitionBackends.TryGet(protocol, out var backend))
        {
            logger.LogWarning("Retaining transfer {TransferId}: no {Protocol} backend is available for cleanup",
                transferId, protocol);
            return false;
        }

        // Re-read the admin setting at the destructive boundary. RefreshAsync is throttled and retains its
        // last known value on failure, so this is cheap while still honoring a recent mode change.
        await libraryPreferences.RefreshAsync(ct);
        var removeData = ShouldRemoveSourceData(libraryPreferences.Current, backend.Capabilities);

        try
        {
            var removed = await backend.RemoveAsync(transferId, removeData, ct);
            if (!removed)
            {
                logger.LogWarning("Backend declined cleanup for imported transfer {TransferId}; source data was retained",
                    transferId);
                return false;
            }

            logger.LogInformation("Removed imported transfer {TransferId} from {Protocol} (removeData={RemoveData})",
                transferId, protocol, removeData);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cleanup of imported transfer {TransferId} failed; source data was retained", transferId);
            return false;
        }
    }

    private void PruneExpiredSuccesses()
    {
        var cutoff = DateTime.UtcNow - SuccessRetention;
        foreach (var pair in _cleanups)
        {
            var completedAt = pair.Value.CompletedAt;
            if (completedAt is not null && completedAt < cutoff)
                _cleanups.TryRemove(pair);
        }
    }

    internal static bool ShouldRemoveSourceData(
        EffectiveLibraryOrganization preferences, AcquisitionBackendCapabilities capabilities) =>
        !capabilities.CanKeepSourceDataAfterRemoval ||
        preferences.TransferMode == TransferMode.Move ||
        (preferences.TransferMode == TransferMode.Copy && preferences.DeleteSourceAfterImport);

    private static bool DestinationsMatch(IReadOnlyList<ImportedFileRecord> files, out string reason)
    {
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.DestinationPath))
            {
                reason = "an imported destination path is empty";
                return false;
            }

            try
            {
                var destination = new FileInfo(Path.GetFullPath(file.DestinationPath));
                if (!destination.Exists)
                {
                    reason = $"imported destination '{file.DestinationPath}' is missing";
                    return false;
                }
                if (destination.Length != file.SizeBytes)
                {
                    reason = $"imported destination '{file.DestinationPath}' has {destination.Length} bytes; expected {file.SizeBytes}";
                    return false;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                reason = $"imported destination '{file.DestinationPath}' cannot be verified: {ex.Message}";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private sealed class CleanupEntry(Lazy<Task<bool>> work)
    {
        private long _completedAtTicks;
        public Lazy<Task<bool>> Work { get; } = work;
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
