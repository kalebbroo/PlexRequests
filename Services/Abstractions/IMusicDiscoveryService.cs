using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Abstractions;

/// <summary>
/// Curated music discovery is intentionally separate from request fulfillment. Playlists and mood shelves
/// are browse containers; the album/artist/track cards inside them remain the requestable media identities.
/// </summary>
public interface IMusicDiscoveryService
{
    Task<MusicBrowseHubDto> GetHubAsync(CancellationToken cancellationToken = default);
    Task<List<MusicPlaylistSummaryDto>> GetCategoryAsync(string token,
        CancellationToken cancellationToken = default);
    Task<MusicPlaylistDto?> GetPlaylistAsync(string playlistId,
        CancellationToken cancellationToken = default);
}

/// <summary>Dedicated music search boundary used by the Music experience. It returns provider-backed,
/// requestable identities grouped by their real kind instead of mixing music into the movie search UI.</summary>
public interface IMusicCatalogSearchService
{
    Task<MusicCatalogSearchResultDto> SearchAsync(string query, MediaKind? kind = null,
        int pageSizePerKind = 16, CancellationToken cancellationToken = default);
}
