using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

public class PlexApiService(IMediaMetadataProvider metadata) : IPlexApiService
{
    private readonly IMediaMetadataProvider _metadata = metadata;

    public Task<List<MediaCardDto>> GetLibraryContentAsync(MediaType mediaType, int page = 1, int pageSize = 20)
        => _metadata.GetLibraryAsync(mediaType, page, pageSize);

    public Task<List<PlexLibrary>> GetLibrariesAsync() => Task.FromResult(new List<PlexLibrary>
    {
        new(){ Id=1, Key="movies", Title="Movies", Type=MediaType.Movie },
        new(){ Id=2, Key="tv", Title="TV Shows", Type=MediaType.TvShow }
    });

    public Task<MediaDetailDto?> GetMediaDetailsAsync(int mediaId, MediaType mediaType)
        => _metadata.GetDetailsAsync(mediaId, mediaType);

    public Task<PlexServerInfo?> GetServerInfoAsync() => Task.FromResult<PlexServerInfo?>(null);

    public Task<List<int>> GetAvailableSeasonsAsync(int tvShowId) => Task.FromResult(new List<int>());

    public Task<bool> IsAvailableOnPlexAsync(int mediaId, MediaType mediaType) => Task.FromResult(false);

    public Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10)
        => _metadata.GetRecentlyAddedAsync(count);

    public Task<List<MediaCardDto>> SearchMediaAsync(string query, MediaType? mediaType = null)
        => _metadata.SearchAsync(query, mediaType);
}
