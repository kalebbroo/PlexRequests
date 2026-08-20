using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;
using Xunit;

namespace PlexRequests.Tests;

public sealed class YouTubeMusicMetadataTests
{
    private const string VisitorHtml = "<script>ytcfg.set({\"VISITOR_DATA\":\"visitor-token\"});</script>";

    [Fact]
    public async Task AlbumSearch_MapsCanonicalCaseSensitiveIdentityArtworkAndArtist()
    {
        const string json = """
            {"contents":{"musicShelfRenderer":{"contents":[{"musicResponsiveListItemRenderer":{
              "flexColumns":[
                {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Random Access Memories"}]}}},
                {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[
                  {"text":"Album"},{"text":" • "},{"text":"Daft Punk","navigationEndpoint":{"browseEndpoint":{"browseId":"Artist_CASE","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ARTIST"}}}}},{"text":" • "},{"text":"2013"}
                ]}}}
              ],
              "navigationEndpoint":{"browseEndpoint":{"browseId":"MPREb_CaseSensitive","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ALBUM"}}}},
              "thumbnail":{"musicThumbnailRenderer":{"thumbnail":{"thumbnails":[
                {"url":"https://img/60","width":60,"height":60},{"url":"https://img/544","width":544,"height":544}
              ]}}}
            }}]}}}
            """;
        var handler = new QueueHandler(Html(VisitorHtml), Json(json));
        var provider = CreateProvider(handler);

        var rows = await provider.SearchAsync(new MediaSearchQuery
        {
            Query = "Daft Punk",
            MediaType = MediaType.Music,
            Kind = MediaKind.Album
        });

        var album = Assert.Single(rows);
        Assert.Equal("Random Access Memories", album.Title);
        Assert.Equal("Daft Punk", album.Subtitle);
        Assert.Equal(2013, album.Year);
        Assert.Equal("https://img/544", album.PosterUrl);
        Assert.Equal("Music:Album:youtube:MPREb_CaseSensitive", album.ResolveMediaRef().StableKey);
        Assert.Contains("youtubei/v1/search", handler.Requests[1], StringComparison.Ordinal);
        Assert.Contains("visitor-token", handler.VisitorHeaders);
    }

    [Fact]
    public async Task AlbumDetails_MapTrackContractUsedByDurableAcquisition()
    {
        const string json = """
            {"contents":{"musicResponsiveHeaderRenderer":{
              "title":{"runs":[{"text":"Discovery"}]},
              "subtitle":{"runs":[{"text":"Album"},{"text":" • "},{"text":"2001"}]},
              "straplineTextOne":{"runs":[{"text":"Daft Punk","navigationEndpoint":{"browseEndpoint":{"browseId":"artist-id","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ARTIST"}}}}}]},
              "buttons":[{"musicPlayButtonRenderer":{"playNavigationEndpoint":{"watchPlaylistEndpoint":{"playlistId":"OLAK-album"}}}}],
              "thumbnail":{"musicThumbnailRenderer":{"thumbnail":{"thumbnails":[{"url":"https://img/album","width":544,"height":544}]}}}
            }},"tracks":{"musicShelfRenderer":{"contents":[
              {"musicResponsiveListItemRenderer":{
                "flexColumns":[
                  {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"One More Time","navigationEndpoint":{"watchEndpoint":{"videoId":"Video_CASE"}}}]}}},
                  {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Daft Punk","navigationEndpoint":{"browseEndpoint":{"browseId":"artist-id","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ARTIST"}}}}}]}}}
                ],
                "fixedColumns":[{"musicResponsiveListItemFixedColumnRenderer":{"text":{"runs":[{"text":"5:20"}]}}}]
              }}
            ]}}}
            """;
        var provider = CreateProvider(new QueueHandler(Html(VisitorHtml), Json(json)));

        var detail = await provider.GetDetailsAsync(
            MediaRef.FromExternal("youtube", "MPREb_Discovery", MediaType.Music, MediaKind.Album));

        Assert.NotNull(detail);
        Assert.Equal("Discovery", detail.Title);
        Assert.Equal("Daft Punk", detail.Music!.ArtistCredit);
        Assert.Equal("artist-id", detail.Music.ArtistId);
        Assert.Equal("OLAK-album", detail.Music.ReleaseId);
        var track = Assert.Single(detail.Music.Tracks);
        Assert.Equal("Video_CASE", track.RecordingId);
        Assert.Equal(320000, track.DurationMs);
        Assert.Equal(1, track.TrackNumber);
    }

    [Fact]
    public async Task TrackDetails_UseWatchMetadataWithoutConsumingPlaybackStreams()
    {
        const string json = """
            {"contents":{"playlistPanelVideoRenderer":{
              "title":{"runs":[{"text":"Instant Crush"}]},"videoId":"Track_CASE",
              "longBylineText":{"runs":[
                {"text":"Daft Punk","navigationEndpoint":{"browseEndpoint":{"browseId":"artist-id","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ARTIST"}}}}},
                {"text":" • "},{"text":"Random Access Memories","navigationEndpoint":{"browseEndpoint":{"browseId":"album-id","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ALBUM"}}}}},
                {"text":" • "},{"text":"2013"}
              ]},
              "lengthText":{"runs":[{"text":"5:38"}]},
              "thumbnail":{"thumbnails":[{"url":"https://img/track","width":544,"height":544}]}
            }}}
            """;
        var handler = new QueueHandler(Html(VisitorHtml), Json(json));
        var provider = CreateProvider(handler);

        var detail = await provider.GetDetailsAsync(
            MediaRef.FromExternal("youtube", "Track_CASE", MediaType.Music, MediaKind.Track));

        Assert.NotNull(detail);
        Assert.Equal("Instant Crush", detail.Title);
        Assert.Equal("Daft Punk", detail.Music!.ArtistCredit);
        Assert.Equal("album-id", detail.Music.ReleaseGroupId);
        Assert.Equal("Random Access Memories", detail.Music.AlbumTitle);
        Assert.Equal(338000, Assert.Single(detail.Music.Tracks).DurationMs);
        Assert.Contains("youtubei/v1/next", handler.Requests[1], StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, x => x.Contains("player", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ArtistDetails_FollowAllAlbumsEndpointForCompleteAcquisitionContract()
    {
        const string artist = """
            {"contents":{"musicImmersiveHeaderRenderer":{"title":{"runs":[{"text":"Prolific Artist"}]}},
            "albums":{"musicCarouselShelfRenderer":{
              "header":{"musicCarouselShelfBasicHeaderRenderer":{"title":{"runs":[{"text":"Albums","navigationEndpoint":{"browseEndpoint":{"browseId":"all-albums","params":"all-params"}}}]}}},
              "contents":[{"musicTwoRowItemRenderer":{"title":{"runs":[{"text":"Preview Album","navigationEndpoint":{"browseEndpoint":{"browseId":"album-preview","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ALBUM"}}}}}]}}}]
            }}}}
            """;
        const string allAlbums = """
            {"contents":{"gridRenderer":{"items":[
              {"musicTwoRowItemRenderer":{"title":{"runs":[{"text":"Preview Album","navigationEndpoint":{"browseEndpoint":{"browseId":"album-preview","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ALBUM"}}}}}]}}},
              {"musicTwoRowItemRenderer":{"title":{"runs":[{"text":"Deep Catalog Album","navigationEndpoint":{"browseEndpoint":{"browseId":"album-deep","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ALBUM"}}}}}]}}}
            ]}}}
            """;
        var handler = new QueueHandler(Html(VisitorHtml), Json(artist), Json(allAlbums));
        var provider = CreateProvider(handler);

        var detail = await provider.GetDetailsAsync(
            MediaRef.FromExternal("youtube", "artist-id", MediaType.Music, MediaKind.Artist));

        Assert.NotNull(detail);
        Assert.Equal(new[] { "Preview Album", "Deep Catalog Album" }, detail.Music!.AlbumTitles);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task TransientFailure_IsRetriedAndRecovers()
    {
        const string json = """
            {"contents":{"musicShelfRenderer":{"contents":[{"musicResponsiveListItemRenderer":{
              "flexColumns":[{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Daft Punk"}]}}}],
              "navigationEndpoint":{"browseEndpoint":{"browseId":"Artist_CASE","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ARTIST"}}}}
            }}]}}}
            """;
        var handler = new QueueHandler(Html(VisitorHtml),
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), Json(json));
        var provider = CreateProvider(handler);

        var rows = await provider.SearchAsync(new MediaSearchQuery
        {
            Query = "Daft Punk",
            MediaType = MediaType.Music,
            Kind = MediaKind.Artist
        });

        Assert.Single(rows);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task BrowseHub_MapsDiscoveryChartsPlaylistsAndGroupedCategories()
    {
        const string explore = """
            {"contents":{"sectionListRenderer":{"contents":[
              {"musicCarouselShelfRenderer":{"header":{"musicCarouselShelfBasicHeaderRenderer":{"title":{"runs":[{"text":"New albums & singles"}]}}},"contents":[
                {"musicTwoRowItemRenderer":{"title":{"runs":[{"text":"New Album","navigationEndpoint":{"browseEndpoint":{"browseId":"album-id","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ALBUM"}}}}}]},"subtitle":{"runs":[{"text":"Album"},{"text":" • "},{"text":"Artist"}]},"thumbnailRenderer":{"musicThumbnailRenderer":{"thumbnail":{"thumbnails":[{"url":"https://img/album","width":544,"height":544}]}}}}}
              ]}},
              {"musicCarouselShelfRenderer":{"header":{"musicCarouselShelfBasicHeaderRenderer":{"title":{"runs":[{"text":"Trending"}]}}},"contents":[
                {"musicResponsiveListItemRenderer":{"playlistItemData":{"videoId":"track-id"},"flexColumns":[
                  {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Trending Song"}]}}},
                  {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Singer","navigationEndpoint":{"browseEndpoint":{"browseId":"artist-id","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ARTIST"}}}}}]}}}
                ]}}
              ]}}
            ]}}}
            """;
        const string charts = """
            {"contents":{"sectionListRenderer":{"contents":[
              {"musicCarouselShelfRenderer":{"header":{"musicCarouselShelfBasicHeaderRenderer":{"title":{"runs":[{"text":"Video charts"}]}}},"contents":[
                {"musicTwoRowItemRenderer":{"title":{"runs":[{"text":"Top 100"}]},"navigationEndpoint":{"browseEndpoint":{"browseId":"VLchart-id"}},"subtitle":{"runs":[{"text":"Playlist • YouTube Music"}]}}}
              ]}},
              {"musicCarouselShelfRenderer":{"header":{"musicCarouselShelfBasicHeaderRenderer":{"title":{"runs":[{"text":"Top artists"}]}}},"contents":[
                {"musicResponsiveListItemRenderer":{"flexColumns":[
                  {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Chart Artist","navigationEndpoint":{"browseEndpoint":{"browseId":"chart-artist","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ARTIST"}}}}}]}}},
                  {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"2M subscribers"}]}}}
                ]}}
              ]}}
            ]}}}
            """;
        const string categories = """
            {"contents":{"gridRenderer":{"header":{"gridHeaderRenderer":{"title":{"runs":[{"text":"Genres"}]}}},"items":[
              {"musicNavigationButtonRenderer":{"buttonText":{"runs":[{"text":"Rock"}]},"clickCommand":{"browseEndpoint":{"browseId":"FEmusic_moods_and_genres_category","params":"opaque-rock"}}}}
            ]}}}
            """;
        var provider = CreateProvider(new QueueHandler(Html(VisitorHtml), Json(explore), Json(charts), Json(categories)));

        var hub = await provider.GetBrowseHubAsync();

        Assert.Equal("New Album", Assert.Single(hub.NewReleases).Title);
        Assert.Equal("Music:Track:youtube:track-id", Assert.Single(hub.TrendingTracks).ResolveMediaRef().StableKey);
        Assert.Equal("Chart Artist", Assert.Single(hub.PopularArtists).Title);
        Assert.Equal("chart-id", Assert.Single(hub.FeaturedPlaylists).Id);
        var genre = Assert.Single(hub.Categories);
        Assert.Equal("Genres", genre.Group);
        Assert.Equal("opaque-rock", genre.Token);
    }

    [Fact]
    public async Task Playlist_MapsVisibleTrackIdentityWithoutMakingPlaylistRequestable()
    {
        const string playlist = """
            {"contents":{"musicResponsiveHeaderRenderer":{"title":{"runs":[{"text":"Coffee Shop Blend"}]},"subtitle":{"runs":[{"text":"Playlist"},{"text":" • "},{"text":"YouTube Music"}]},"thumbnail":{"musicThumbnailRenderer":{"thumbnail":{"thumbnails":[{"url":"https://img/list","width":544,"height":544}]}}}},
            "tracks":{"musicShelfRenderer":{"contents":[{"musicResponsiveListItemRenderer":{"playlistItemData":{"videoId":"Visible_CASE"},"flexColumns":[
              {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Visible Song"}]}}},
              {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Visible Artist","navigationEndpoint":{"browseEndpoint":{"browseId":"artist","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ARTIST"}}}}}]}}}
            ]}}]}}}}
            """;
        var handler = new QueueHandler(Html(VisitorHtml), Json(playlist));
        var provider = CreateProvider(handler);

        var result = await provider.GetPlaylistAsync("VLplaylist-id");

        Assert.NotNull(result);
        Assert.Equal("playlist-id", result.Id);
        Assert.Equal("Coffee Shop Blend", result.Title);
        Assert.Equal("Music:Track:youtube:Visible_CASE", Assert.Single(result.Tracks).ResolveMediaRef().StableKey);
        Assert.Contains("youtubei/v1/browse", handler.Requests[1], StringComparison.Ordinal);
    }

    private static YouTubeMusicMetadataProvider CreateProvider(HttpMessageHandler handler)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new YouTubeMusicMetadataProvider(
            new HttpClient(handler) { BaseAddress = new Uri("https://music.youtube.com/") }, cache,
            NullLogger<YouTubeMusicMetadataProvider>.Instance);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
    };

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<string> Requests { get; } = new();
        public List<string> VisitorHeaders { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            if (request.Headers.TryGetValues("X-Goog-Visitor-Id", out var values))
                VisitorHeaders.AddRange(values);
            if (_responses.Count == 0) throw new InvalidOperationException("No fake response remains.");
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
