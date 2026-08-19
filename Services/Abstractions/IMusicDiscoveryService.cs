using PlexRequestsHosted.Shared.DTOs;

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
