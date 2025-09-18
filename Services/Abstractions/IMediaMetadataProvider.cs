using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Abstractions;

public interface IMediaMetadataProvider
{
    Task<List<MediaCardDto>> SearchAsync(string query, MediaType? mediaType = null, int page = 1, int pageSize = 20);
    Task<MediaDetailDto?> GetDetailsAsync(int mediaId, MediaType mediaType);
    Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10);
    Task<List<MediaCardDto>> GetLibraryAsync(MediaType mediaType, int page = 1, int pageSize = 20);
}
