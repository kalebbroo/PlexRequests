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
}
