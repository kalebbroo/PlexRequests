using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

public sealed class MusicDiscoveryService(
    IMusicSettingsService settings,
    IMediaMetadataProvider metadata,
    YouTubeMusicMetadataProvider youtube) : IMusicDiscoveryService
{
    public async Task<MusicBrowseHubDto> GetHubAsync(CancellationToken cancellationToken = default)
    {
        var current = await settings.GetAsync(cancellationToken);
        if (!current.CatalogEnabled) return new();

        if (current.MetadataProvider.Equals("youtube", StringComparison.OrdinalIgnoreCase))
            return await youtube.GetBrowseHubAsync(cancellationToken);

        // MusicBrainz has no playlist/mood concept. Keep the same page contract and fill the requestable
        // shelves from its standards-based ListenBrainz discovery feed instead of inventing fake playlists.
        var trending = metadata.GetTrendingAsync(MediaType.Music, 1, 40);
        var popular = metadata.GetPopularAsync(MediaType.Music, 1, 40);
        var artists = metadata.SearchAsync(new Shared.Media.MediaSearchQuery
        {
            Query = "popular",
            MediaType = MediaType.Music,
            Kind = MediaKind.Artist,
            PageSize = 24
        });
        await Task.WhenAll(trending, popular, artists);
        return new MusicBrowseHubDto
        {
            SourceLabel = "MusicBrainz & ListenBrainz",
            TrendingTracks = trending.Result,
            NewReleases = popular.Result,
            PopularArtists = artists.Result,
            Categories = DefaultGenres.Select(x => new MusicCategoryDto
            {
                Title = x,
                Group = "Genres",
                Token = "search:" + x
            }).ToList()
        };
    }

    public async Task<List<MusicPlaylistSummaryDto>> GetCategoryAsync(string token,
        CancellationToken cancellationToken = default)
    {
        var current = await settings.GetAsync(cancellationToken);
        return current.CatalogEnabled
               && current.MetadataProvider.Equals("youtube", StringComparison.OrdinalIgnoreCase)
            ? await youtube.GetMoodPlaylistsAsync(token, cancellationToken)
            : new();
    }

    public async Task<MusicPlaylistDto?> GetPlaylistAsync(string playlistId,
        CancellationToken cancellationToken = default)
    {
        var current = await settings.GetAsync(cancellationToken);
        return current.CatalogEnabled
               && current.MetadataProvider.Equals("youtube", StringComparison.OrdinalIgnoreCase)
            ? await youtube.GetPlaylistAsync(playlistId, cancellationToken)
            : null;
    }

    private static readonly string[] DefaultGenres =
    [
        "Rock", "Pop", "Hip-Hop", "Electronic", "Jazz", "Classical", "Country", "R&B",
        "Metal", "Folk", "Reggae", "Blues", "Latin", "Soundtrack"
    ];
}
