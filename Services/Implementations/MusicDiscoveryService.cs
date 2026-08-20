using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

public sealed class MusicDiscoveryService(
    IMusicSettingsService settings,
    IMediaMetadataProvider metadata,
    YouTubeMusicMetadataProvider youtube,
    ILogger<MusicDiscoveryService> logger) : IMusicDiscoveryService
{
    public async Task<MusicBrowseHubDto> GetHubAsync(CancellationToken cancellationToken = default)
    {
        var current = await settings.GetAsync(cancellationToken);
        if (!current.CatalogEnabled) return new();

        // Discovery and catalog search are deliberately separate capabilities. YouTube Music has the
        // visual charts, artwork, playlists and mood taxonomy that a browse page needs, while the admin
        // may still prefer MusicBrainz as the canonical search/identity provider. Tying the hub to that
        // search setting previously made the two headline shelves identical and left every artist blank.
        try
        {
            var youtubeHub = await youtube.GetBrowseHubAsync(cancellationToken);
            NormalizeShelves(youtubeHub);
            if (HasDiscovery(youtubeHub)) return youtubeHub;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "YouTube Music discovery failed; using the metadata catalog fallback");
        }

        // MusicBrainz has no playlist/mood concept and currently exposes the same ListenBrainz data for
        // both Trending and Popular. Show that feed once instead of presenting a duplicate as new releases.
        var trending = metadata.GetTrendingAsync(MediaType.Music, 1, 40);
        await trending;
        return new MusicBrowseHubDto
        {
            SourceLabel = "MusicBrainz & ListenBrainz",
            TrendingTracks = trending.Result,
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
            ? await youtube.GetMoodPlaylistsAsync(token, cancellationToken)
            : new();
    }

    public async Task<MusicPlaylistDto?> GetPlaylistAsync(string playlistId,
        CancellationToken cancellationToken = default)
    {
        var current = await settings.GetAsync(cancellationToken);
        return current.CatalogEnabled
            ? await youtube.GetPlaylistAsync(playlistId, cancellationToken)
            : null;
    }

    private static bool HasDiscovery(MusicBrowseHubDto hub)
        => hub.TrendingTracks.Count + hub.NewReleases.Count + hub.PopularArtists.Count
           + hub.FeaturedPlaylists.Count + hub.Categories.Count > 0;

    private static void NormalizeShelves(MusicBrowseHubDto hub)
    {
        hub.TrendingTracks = Distinct(hub.TrendingTracks);
        hub.NewReleases = Distinct(hub.NewReleases);
        hub.PopularArtists = Distinct(hub.PopularArtists);

        // YouTube occasionally promotes an album and its title track in both hero shelves. A browse hub
        // should offer new choices as the user scrolls, so remove cross-shelf title/artist duplicates.
        var trending = hub.TrendingTracks.Select(DisplayKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        hub.NewReleases = hub.NewReleases.Where(item => !trending.Contains(DisplayKey(item))).ToList();
    }

    private static List<MediaCardDto> Distinct(IEnumerable<MediaCardDto> items)
        => items.Where(item => item.ResolveMediaRef().IsValid)
            .GroupBy(item => item.ResolveMediaRef().StableKey, StringComparer.Ordinal)
            .Select(group => group.First()).ToList();

    private static string DisplayKey(MediaCardDto item)
        => item.Title.Trim();

    private static readonly string[] DefaultGenres =
    [
        "Rock", "Pop", "Hip-Hop", "Electronic", "Jazz", "Classical", "Country", "R&B",
        "Metal", "Folk", "Reggae", "Blues", "Latin", "Soundtrack"
    ];
}
