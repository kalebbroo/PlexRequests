using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Organize;
using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Import;

public interface ILibraryImporter
{
    /// <summary>Organize a finished transfer's payload into the Plex library per the admin-configured
    /// naming/transfer preferences, and report what actually happened.</summary>
    Task<ImportResult> ImportAsync(FulfillmentJobDto job, TransferItem transfer, string sourcePath, CancellationToken ct);
}

/// <summary>Thin adapter: resolves the current library-organization preferences and delegates to
/// <see cref="ILibraryOrganizer"/>, which does the actual extraction/splitting/naming/transfer.</summary>
public class LibraryImporter(ILibraryOrganizer organizer, ILibraryOrganizationProvider prefsProvider) : ILibraryImporter
{
    public async Task<ImportResult> ImportAsync(FulfillmentJobDto job, TransferItem transfer, string sourcePath, CancellationToken ct)
    {
        var result = await organizer.OrganizeAsync(job, transfer, sourcePath, prefsProvider.Current, ct);
        if (!result.Success) return result;

        // An organizer result is not success until every recorded destination can be read back at the size
        // we staged. This keeps a disconnected/short-writing NAS from becoming an Imported row and a false
        // "Available" notification merely because File.Copy returned.
        foreach (var file in result.Files)
        {
            try
            {
                var actualLength = new FileInfo(file.DestinationPath).Length;
                if (actualLength != file.SizeBytes)
                    return ImportResult.Fail($"Import verification failed for '{file.DestinationPath}': expected {file.SizeBytes} bytes, found {actualLength} bytes");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ImportResult.Fail($"Import verification failed for '{file.DestinationPath}': {ex.Message}");
            }
        }

        return result;
    }
}
