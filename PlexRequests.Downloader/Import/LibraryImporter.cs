using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Organize;
using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Import;

public interface ILibraryImporter
{
    /// <summary>Organize a finished torrent's payload into the Plex library per the admin-configured
    /// naming/transfer preferences, and report what actually happened.</summary>
    Task<ImportResult> ImportAsync(FulfillmentJobDto job, TorrentItem torrent, string sourcePath, CancellationToken ct);
}

/// <summary>Thin adapter: resolves the current library-organization preferences and delegates to
/// <see cref="ILibraryOrganizer"/>, which does the actual extraction/splitting/naming/transfer.</summary>
public class LibraryImporter(ILibraryOrganizer organizer, ILibraryOrganizationProvider prefsProvider) : ILibraryImporter
{
    public Task<ImportResult> ImportAsync(FulfillmentJobDto job, TorrentItem torrent, string sourcePath, CancellationToken ct) =>
        organizer.OrganizeAsync(job, torrent, sourcePath, prefsProvider.Current, ct);
}
