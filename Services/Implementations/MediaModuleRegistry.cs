using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Services.Implementations;

public sealed class MediaModuleRegistry : IMediaModuleRegistry
{
    private readonly IReadOnlyDictionary<MediaType, MediaModuleDescriptor> _modules;

    public MediaModuleRegistry(IEnumerable<IMediaModule> modules)
    {
        var all = modules.Select(x => x.Descriptor).ToList();
        var duplicate = all.GroupBy(x => x.MediaType).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"More than one media module registered for {duplicate.Key}.");
        _modules = all.ToDictionary(x => x.MediaType);
        All = all.AsReadOnly();
        Enabled = all.Where(x => x.Enabled).ToList().AsReadOnly();
    }

    public IReadOnlyList<MediaModuleDescriptor> All { get; }
    public IReadOnlyList<MediaModuleDescriptor> Enabled { get; }

    public MediaModuleDescriptor Get(MediaType mediaType) => _modules.TryGetValue(mediaType, out var module)
        ? module
        : throw new KeyNotFoundException($"No media module is registered for {mediaType}.");

    public bool IsEnabled(MediaType mediaType) => _modules.TryGetValue(mediaType, out var module) && module.Enabled;
}

public sealed class MovieMediaModule : IMediaModule
{
    public MediaModuleDescriptor Descriptor { get; } = new()
    {
        Key = "movies", DisplayName = "Movies", MediaType = MediaType.Movie,
        Kinds = [MediaKind.Movie], RequestScopes = [RequestScopeKind.Title], Enabled = true,
        Capabilities = MediaModuleCapabilities.Search | MediaModuleCapabilities.Discovery
                       | MediaModuleCapabilities.Request | MediaModuleCapabilities.AutomaticFulfillment
                       | MediaModuleCapabilities.InteractiveSearch | MediaModuleCapabilities.LibraryImport
                       | MediaModuleCapabilities.PlexVerification
    };
}

public sealed class TelevisionMediaModule : IMediaModule
{
    public MediaModuleDescriptor Descriptor { get; } = new()
    {
        Key = "television", DisplayName = "TV Shows", MediaType = MediaType.TvShow,
        Kinds = [MediaKind.Series],
        RequestScopes = [RequestScopeKind.Series, RequestScopeKind.Seasons, RequestScopeKind.Episodes],
        Enabled = true,
        Capabilities = MediaModuleCapabilities.Search | MediaModuleCapabilities.Discovery
                       | MediaModuleCapabilities.Request | MediaModuleCapabilities.AutomaticFulfillment
                       | MediaModuleCapabilities.InteractiveSearch | MediaModuleCapabilities.LibraryImport
                       | MediaModuleCapabilities.PlexVerification | MediaModuleCapabilities.Monitoring
    };
}

public sealed class AnimeMediaModule : IMediaModule
{
    public MediaModuleDescriptor Descriptor { get; } = new()
    {
        Key = "anime", DisplayName = "Anime", MediaType = MediaType.Anime,
        Kinds = [MediaKind.Series],
        RequestScopes = [RequestScopeKind.Series, RequestScopeKind.Seasons, RequestScopeKind.Episodes],
        Enabled = true,
        Capabilities = MediaModuleCapabilities.Search | MediaModuleCapabilities.Request
                       | MediaModuleCapabilities.AutomaticFulfillment | MediaModuleCapabilities.InteractiveSearch
                       | MediaModuleCapabilities.LibraryImport | MediaModuleCapabilities.PlexVerification
    };
}

/// <summary>
/// Registered now so every core consumer can reason about music, but intentionally disabled until the later
/// PRs provide metadata, acquisition, import and Plex verification end to end.
/// </summary>
public sealed class MusicMediaModule : IMediaModule
{
    public MediaModuleDescriptor Descriptor { get; } = new()
    {
        Key = "music", DisplayName = "Music", MediaType = MediaType.Music,
        Kinds = [MediaKind.Album, MediaKind.Artist, MediaKind.Track],
        RequestScopes = [RequestScopeKind.Album, RequestScopeKind.ArtistCatalog, RequestScopeKind.Track],
        Enabled = false,
        Capabilities = MediaModuleCapabilities.None,
        UnavailableReason = "Music fulfillment is still being installed."
    };
}
