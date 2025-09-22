using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.MetadataProviders;

public class TraktMetadataProvider : IMediaMetadataProvider
{
    private readonly ILogger<TraktMetadataProvider> _logger;

    public TraktMetadataProvider(IConfiguration configuration, ILogger<TraktMetadataProvider> logger)
    {
        _logger = logger;
        // TODO: Implement Trakt API client configuration
    }

    public Task<List<MediaCardDto>> SearchAsync(string query, MediaType? mediaType = null, int page = 1, int pageSize = 20)
    {
        _logger.LogWarning("Trakt provider not yet implemented, returning empty results");
        return Task.FromResult(new List<MediaCardDto>());
    }

    public Task<MediaDetailDto?> GetDetailsAsync(int mediaId, MediaType mediaType)
    {
        _logger.LogWarning("Trakt provider not yet implemented, returning null");
        return Task.FromResult<MediaDetailDto?>(null);
    }

    public Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10)
    {
        _logger.LogWarning("Trakt provider not yet implemented, returning empty results");
        return Task.FromResult(new List<MediaCardDto>());
    }

    public Task<List<MediaCardDto>> GetLibraryAsync(MediaType mediaType, int page = 1, int pageSize = 20)
    {
        _logger.LogWarning("Trakt provider not yet implemented, returning empty results");
        return Task.FromResult(new List<MediaCardDto>());
    }
}
