using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// TheTVDB metadata provider — strong for TV (and movies). Requires an API key, so it's only selectable
/// when <c>ApiKeys:Tvdb:ApiKey</c> is configured; otherwise the router falls back to a keyless provider.
/// Scaffold only: capabilities are declared and calls return empty. Real TVDB v4 API calls are TODOs.
/// </summary>
public class TvdbMetadataProvider : IMediaMetadataProvider
{
    private readonly ILogger<TvdbMetadataProvider> _logger;
    private readonly bool _hasKey;

    public TvdbMetadataProvider(IConfiguration configuration, ILogger<TvdbMetadataProvider> logger)
    {
        _logger = logger;
        _hasKey = !string.IsNullOrWhiteSpace(configuration["ApiKeys:Tvdb:ApiKey"]);
        // TODO(tvdb): login to https://api4.thetvdb.com/v4/login with the API key to get a bearer token.
    }

    public string ProviderKey => "tvdb";
    public bool RequiresApiKey => true;
    public bool IsAvailable => _hasKey;
    public bool Supports(MediaType mediaType) => mediaType is MediaType.Movie or MediaType.TvShow or MediaType.Anime;

    public Task<List<MediaCardDto>> SearchAsync(string query, MediaType? mediaType = null, int page = 1, int pageSize = 20)
    {
        // TODO(tvdb): GET /v4/search?query={query}&type=series|movie -> map to MediaCardDto.
        _logger.LogDebug("TVDB SearchAsync stub for '{Query}' — not yet implemented", query);
        return Task.FromResult(new List<MediaCardDto>());
    }

    public Task<MediaDetailDto?> GetDetailsAsync(int mediaId, MediaType mediaType)
    {
        // TODO(tvdb): GET /v4/series/{id}/extended or /v4/movies/{id}/extended -> MediaDetailDto
        // (including seasons/episodes and remoteIds for imdb/tmdb cross-refs).
        return Task.FromResult<MediaDetailDto?>(null);
    }

    public Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10) => Task.FromResult(new List<MediaCardDto>());
    public Task<List<MediaCardDto>> GetLibraryAsync(MediaType mediaType, int page = 1, int pageSize = 20) => Task.FromResult(new List<MediaCardDto>());
    public Task<string?> GetImdbIdAsync(int mediaId, MediaType mediaType)
        // TODO(tvdb): the extended endpoints return remoteIds; pull the IMDb id from there.
        => Task.FromResult<string?>(null);
}
