using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// Small, read-only C# client for the public YouTube Music web catalog. It deliberately implements only
/// the metadata surface Plex Requests needs (search and public artist/album/track details); authenticated
/// library mutation, playback URLs, and media downloading do not belong in this provider.
/// </summary>
public sealed partial class YouTubeMusicMetadataProvider(
    HttpClient http,
    IMemoryCache cache,
    ILogger<YouTubeMusicMetadataProvider> logger) : IMediaMetadataProvider
{
    private const string Provider = "youtube";
    private const string SearchPath = "youtubei/v1/search?alt=json";
    private const string BrowsePath = "youtubei/v1/browse?alt=json";
    private const string NextPath = "youtubei/v1/next?alt=json";
    private static readonly TimeSpan SearchFreshFor = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DetailFreshFor = TimeSpan.FromHours(12);
    private static readonly TimeSpan StaleFor = TimeSpan.FromDays(7);
    private readonly SemaphoreSlim _visitorLock = new(1, 1);
    private string? _visitorId;

    public string ProviderKey => Provider;
    public bool RequiresApiKey => false;
    public bool IsAvailable => true;
    public bool Supports(MediaType mediaType) => mediaType == MediaType.Music;

    public Task<List<MediaCardDto>> SearchAsync(string query, MediaType? mediaType = null, int page = 1,
        int pageSize = 20) => SearchAsync(new MediaSearchQuery
        {
            Query = query,
            MediaType = mediaType ?? MediaType.Music,
            Kind = MediaKind.Album,
            Page = page,
            PageSize = pageSize
        });

    public async Task<List<MediaCardDto>> SearchAsync(MediaSearchQuery query)
    {
        if (query.MediaType is not null && query.MediaType != MediaType.Music) return new();
        if (string.IsNullOrWhiteSpace(query.Query)) return new();

        var kind = query.Kind is MediaKind.Artist or MediaKind.Track ? query.Kind.Value : MediaKind.Album;
        var page = Math.Max(1, query.Page);
        var take = Math.Clamp(query.PageSize, 1, 50);
        var required = page * take;
        var normalizedQuery = query.Query.Trim();
        var cacheKey = $"ytm:search:{kind}:{page}:{take}:{normalizedQuery.ToLowerInvariant()}";
        var filterParams = kind switch
        {
            MediaKind.Artist => "EgWKAQIgAWoMEA4QChADEAQQCRAF",
            MediaKind.Track => "EgWKAQIIAWoMEA4QChADEAQQCRAF",
            _ => "EgWKAQIYAWoMEA4QChADEAQQCRAF"
        };
        var body = new Dictionary<string, object?> { ["query"] = normalizedQuery, ["params"] = filterParams };

        using var first = await PostJsonAsync(SearchPath, body, cacheKey, SearchFreshFor);
        if (first is null) return new();
        var rows = ParseSearch(first.RootElement, kind);
        var continuation = FindContinuation(first.RootElement);

        // YouTube returns a continuation token rather than an offset. Walk only as far as the requested
        // page (and cap the walk) so a UI request can never fan out into an unbounded crawl.
        for (var hop = 0; rows.Count < required && continuation is not null && hop < 5; hop++)
        {
            var continuationPath = $"{SearchPath}&ctoken={Uri.EscapeDataString(continuation)}&continuation={Uri.EscapeDataString(continuation)}";
            using var next = await PostJsonAsync(continuationPath, body,
                $"{cacheKey}:continuation:{hop}:{continuation.GetHashCode(StringComparison.Ordinal)}", SearchFreshFor);
            if (next is null) break;
            var added = ParseSearch(next.RootElement, kind);
            if (added.Count == 0) break;
            rows.AddRange(added);
            continuation = FindContinuation(next.RootElement);
        }

        return rows.GroupBy(x => x.ResolveMediaRef().StableKey, StringComparer.Ordinal)
            .Select(x => x.First()).Skip((page - 1) * take).Take(take).ToList();
    }

    public Task<MediaDetailDto?> GetDetailsAsync(int mediaId, MediaType mediaType)
        => Task.FromResult<MediaDetailDto?>(null);

    public async Task<MediaDetailDto?> GetDetailsAsync(MediaRef mediaRef)
    {
        if (!mediaRef.IsValid || mediaRef.MediaType != MediaType.Music
            || !mediaRef.Provider.Equals(Provider, StringComparison.OrdinalIgnoreCase)) return null;

        return mediaRef.Kind switch
        {
            MediaKind.Album => await GetAlbumAsync(mediaRef),
            MediaKind.Artist => await GetArtistAsync(mediaRef),
            MediaKind.Track => await GetTrackAsync(mediaRef),
            _ => null
        };
    }

    public Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10)
        => Task.FromResult(new List<MediaCardDto>());

    public Task<List<MediaCardDto>> GetLibraryAsync(MediaType mediaType, int page = 1, int pageSize = 20)
        => Task.FromResult(new List<MediaCardDto>());

    public async Task<List<MediaCardDto>> GetTrendingAsync(MediaType? mediaType = null, int page = 1,
        int pageSize = 20)
    {
        if (mediaType is not null && mediaType != MediaType.Music) return new();
        using var doc = await GetExploreAsync();
        if (doc is null) return new();
        var shelf = FindCarousel(doc.RootElement, "Trending");
        return Page(shelf.HasValue ? MapTrackRows(shelf.Value) : new(), page, pageSize);
    }

    public async Task<List<MediaCardDto>> GetPopularAsync(MediaType mediaType, int page = 1, int pageSize = 20)
    {
        if (mediaType != MediaType.Music) return new();
        using var doc = await GetExploreAsync();
        if (doc is null) return new();
        var shelf = FindCarousel(doc.RootElement, "New albums");
        return Page(shelf.HasValue ? MapAlbumTiles(shelf.Value, null) : new(), page, pageSize);
    }

    public async Task<List<MediaCardDto>> GetTopRatedAsync(MediaType mediaType, int page = 1, int pageSize = 20)
    {
        if (mediaType != MediaType.Music) return new();
        using var doc = await GetChartsAsync();
        if (doc is null) return new();
        var shelf = FindCarousel(doc.RootElement, "Top artists");
        return Page(shelf.HasValue ? MapArtistRows(shelf.Value) : new(), page, pageSize);
    }

    public Task<List<MediaCardDto>> GetByGenreAsync(MediaType mediaType, string genre, int page = 1,
        int pageSize = 20) => Task.FromResult(new List<MediaCardDto>());

    public async Task<MusicBrowseHubDto> GetBrowseHubAsync(CancellationToken cancellationToken = default)
    {
        var exploreTask = GetExploreAsync(cancellationToken);
        var chartsTask = GetChartsAsync(cancellationToken);
        var categoriesTask = PostJsonAsync(BrowsePath,
            new Dictionary<string, object?> { ["browseId"] = "FEmusic_moods_and_genres" },
            "ytm:discovery:categories", DetailFreshFor, cancellationToken);
        await Task.WhenAll(exploreTask, chartsTask, categoriesTask);

        using var explore = exploreTask.Result;
        using var charts = chartsTask.Result;
        using var categories = categoriesTask.Result;
        var hub = new MusicBrowseHubDto { SourceLabel = "YouTube Music" };

        if (explore is not null)
        {
            var newReleases = FindCarousel(explore.RootElement, "New albums");
            if (newReleases.HasValue) hub.NewReleases = MapAlbumTiles(newReleases.Value, null);
            var trending = FindCarousel(explore.RootElement, "Trending");
            if (trending.HasValue) hub.TrendingTracks = MapTrackRows(trending.Value);
        }

        if (charts is not null)
        {
            var artists = FindCarousel(charts.RootElement, "Top artists");
            if (artists.HasValue) hub.PopularArtists = MapArtistRows(artists.Value);
            foreach (var heading in new[] { "Video charts", "Genres" })
            {
                var shelf = FindCarousel(charts.RootElement, heading);
                if (shelf.HasValue) hub.FeaturedPlaylists.AddRange(MapPlaylistTiles(shelf.Value));
            }
            hub.FeaturedPlaylists = DeduplicatePlaylists(hub.FeaturedPlaylists);
        }

        if (categories is not null) hub.Categories = ParseCategories(categories.RootElement);
        // Explore already carries a compact mood row. Use it if YouTube changes the full categories page.
        if (hub.Categories.Count == 0 && explore is not null)
            hub.Categories = ParseCategories(explore.RootElement);
        return hub;
    }

    private Task<JsonDocument?> GetExploreAsync(CancellationToken cancellationToken = default)
        => PostJsonAsync(BrowsePath,
            new Dictionary<string, object?> { ["browseId"] = "FEmusic_explore" },
            "ytm:discovery:explore", SearchFreshFor, cancellationToken);

    private Task<JsonDocument?> GetChartsAsync(CancellationToken cancellationToken = default)
        => PostJsonAsync(BrowsePath, new Dictionary<string, object?>
        {
            ["browseId"] = "FEmusic_charts",
            ["formData"] = new Dictionary<string, object?> { ["selectedValues"] = new[] { "US" } }
        }, "ytm:discovery:charts:US", SearchFreshFor, cancellationToken);

    public async Task<List<MusicPlaylistSummaryDto>> GetMoodPlaylistsAsync(string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 512) return new();
        using var doc = await PostJsonAsync(BrowsePath, new Dictionary<string, object?>
        {
            ["browseId"] = "FEmusic_moods_and_genres_category",
            ["params"] = token
        }, $"ytm:discovery:category:{token.GetHashCode(StringComparison.Ordinal)}", SearchFreshFor,
            cancellationToken);
        return doc is null ? new() : MapPlaylistTiles(doc.RootElement);
    }

    public async Task<MusicPlaylistDto?> GetPlaylistAsync(string playlistId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId) || playlistId.Length > 200) return null;
        var normalized = NormalizePlaylistId(playlistId);
        using var doc = await PostJsonAsync(BrowsePath,
            new Dictionary<string, object?> { ["browseId"] = "VL" + normalized },
            $"ytm:discovery:playlist:{normalized}", SearchFreshFor, cancellationToken);
        if (doc is null) return null;

        JsonElement header;
        if (!TryFindObject(doc.RootElement, "musicResponsiveHeaderRenderer", out header)
            && !TryFindObject(doc.RootElement, "musicDetailHeaderRenderer", out header)) return null;
        var title = FirstRunText(header, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;
        var tracks = MapTrackRows(doc.RootElement);
        return new MusicPlaylistDto
        {
            Id = normalized,
            Title = title,
            Subtitle = JoinRuns(DirectRuns(header, "subtitle")),
            Description = JoinRuns(DirectRuns(header, "description")),
            ArtworkUrl = LargestThumbnail(header),
            TrackCount = tracks.Count,
            Tracks = tracks
        };
    }

    public Task<string?> GetImdbIdAsync(int mediaId, MediaType mediaType) => Task.FromResult<string?>(null);
    public Task<string?> GetImdbIdAsync(MediaRef mediaRef) => Task.FromResult<string?>(null);

    public async Task<List<MediaCardDto>> GetSimilarAsync(MediaRef mediaRef, int count = 12)
    {
        var detail = await GetDetailsAsync(mediaRef);
        var artistId = mediaRef.Kind == MediaKind.Artist ? mediaRef.Id : detail?.Music?.ArtistId;
        if (string.IsNullOrWhiteSpace(artistId)) return new();
        using var doc = await PostJsonAsync(BrowsePath, new Dictionary<string, object?> { ["browseId"] = artistId },
            $"ytm:detail:artist:{artistId}", DetailFreshFor);
        if (doc is null) return new();
        var albums = await ParseCompleteArtistAlbumsAsync(doc.RootElement, artistId,
            detail?.Music?.ArtistCredit ?? detail?.Title);
        return albums
            .Where(x => x.ResolveMediaRef().StableKey != mediaRef.StableKey).Take(Math.Clamp(count, 1, 50)).ToList();
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["query"] = "Daft Punk",
            ["params"] = "EgWKAQIgAWoMEA4QChADEAQQCRAF"
        };
        using var doc = await PostJsonAsync(SearchPath, body,
            $"ytm:health:{DateTime.UtcNow:yyyyMMddHHmm}", TimeSpan.FromMinutes(1), cancellationToken);
        return doc is not null && ParseSearch(doc.RootElement, MediaKind.Artist).Count > 0;
    }

    private async Task<MediaDetailDto?> GetAlbumAsync(MediaRef mediaRef)
    {
        using var doc = await PostJsonAsync(BrowsePath,
            new Dictionary<string, object?> { ["browseId"] = mediaRef.Id },
            $"ytm:detail:album:{mediaRef.Id}", DetailFreshFor);
        if (doc is null || !TryFindObject(doc.RootElement, "musicResponsiveHeaderRenderer", out var header))
            return null;

        var title = FirstRunText(header, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;
        var subtitleRuns = DirectRuns(header, "subtitle");
        var straplineRuns = DirectRuns(header, "straplineTextOne");
        var artists = LinkedNames(straplineRuns.Count > 0 ? straplineRuns : subtitleRuns, "MUSIC_PAGE_TYPE_ARTIST");
        var artist = JoinNames(artists.Select(x => x.Name));
        var year = FindYear(subtitleRuns);
        var primaryType = subtitleRuns.Select(RunText).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var tracks = new List<MusicTrackMetadataDto>();
        if (TryFindObject(doc.RootElement, "musicShelfRenderer", out var shelf))
            tracks = ParseAlbumTracks(shelf, artist);
        var artwork = LargestThumbnail(header);
        var description = TryFindObject(doc.RootElement, "musicDescriptionShelfRenderer", out var descriptionShelf)
            ? JoinRuns(DirectRuns(descriptionShelf, "description"))
            : null;
        var totalMs = tracks.Where(x => x.DurationMs.HasValue).Sum(x => (long)x.DurationMs!.Value);
        var artistId = artists.Select(x => x.Id).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var playlistId = FindFirstString(header, "playlistId");
        var canonical = MediaRef.FromExternal(Provider, mediaRef.Id, MediaType.Music, MediaKind.Album);

        return new MediaDetailDto
        {
            Title = title,
            Subtitle = artist,
            Overview = description ?? Describe(primaryType, artist),
            PosterUrl = artwork,
            BackdropUrl = artwork,
            Year = year,
            Runtime = totalMs > 0 ? Math.Max(1, (int)Math.Ceiling(totalMs / 60000d)) : null,
            MediaType = MediaType.Music,
            ExternalId = canonical.Id,
            ExternalSource = Provider,
            MediaRef = canonical,
            Music = new MusicMetadataDto
            {
                Kind = MediaKind.Album,
                ArtistId = artistId,
                ArtistCredit = artist,
                PrimaryType = primaryType,
                ReleaseGroupId = canonical.Id,
                ReleaseId = playlistId,
                TrackCount = tracks.Count,
                Tracks = tracks
            }
        };
    }

    private async Task<MediaDetailDto?> GetArtistAsync(MediaRef mediaRef)
    {
        using var doc = await PostJsonAsync(BrowsePath,
            new Dictionary<string, object?> { ["browseId"] = mediaRef.Id },
            $"ytm:detail:artist:{mediaRef.Id}", DetailFreshFor);
        if (doc is null) return null;
        if (!TryFindObject(doc.RootElement, "musicImmersiveHeaderRenderer", out var header)
            && !TryFindObject(doc.RootElement, "musicVisualHeaderRenderer", out header)
            && !TryFindObject(doc.RootElement, "musicResponsiveHeaderRenderer", out header)) return null;

        var title = FirstRunText(header, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;
        var description = JoinRuns(DirectRuns(header, "description"));
        var albums = await ParseCompleteArtistAlbumsAsync(doc.RootElement, mediaRef.Id, title);
        var canonical = MediaRef.FromExternal(Provider, mediaRef.Id, MediaType.Music, MediaKind.Artist);
        var artwork = LargestThumbnail(header);
        return new MediaDetailDto
        {
            Title = title,
            Overview = description,
            PosterUrl = artwork,
            BackdropUrl = artwork,
            MediaType = MediaType.Music,
            ExternalId = canonical.Id,
            ExternalSource = Provider,
            MediaRef = canonical,
            Music = new MusicMetadataDto
            {
                Kind = MediaKind.Artist,
                ArtistId = canonical.Id,
                ArtistCredit = title,
                AlbumTitles = albums.Select(x => x.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            }
        };
    }

    private async Task<MediaDetailDto?> GetTrackAsync(MediaRef mediaRef)
    {
        var body = new Dictionary<string, object?>
        {
            ["enablePersistentPlaylistPanel"] = true,
            ["isAudioOnly"] = true,
            ["tunerSettingValue"] = "AUTOMIX_SETTING_NORMAL",
            ["videoId"] = mediaRef.Id,
            ["playlistId"] = "RDAMVM" + mediaRef.Id,
            ["watchEndpointMusicSupportedConfigs"] = new Dictionary<string, object?>
            {
                ["watchEndpointMusicConfig"] = new Dictionary<string, object?>
                {
                    ["hasPersistentPlaylistPanel"] = true,
                    ["musicVideoType"] = "MUSIC_VIDEO_TYPE_ATV"
                }
            }
        };
        using var doc = await PostJsonAsync(NextPath, body, $"ytm:detail:track:{mediaRef.Id}", DetailFreshFor);
        if (doc is null) return null;
        var item = FindObjects(doc.RootElement, "playlistPanelVideoRenderer")
            .FirstOrDefault(x => string.Equals(DirectString(x, "videoId"), mediaRef.Id, StringComparison.Ordinal));
        if (item.ValueKind != JsonValueKind.Object) return null;
        var title = FirstRunText(item, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;
        var byline = DirectRuns(item, "longBylineText");
        var artists = LinkedNames(byline, "MUSIC_PAGE_TYPE_ARTIST");
        var artist = JoinNames(artists.Select(x => x.Name)) ?? FirstRunText(item, "shortBylineText");
        var album = LinkedNames(byline, "MUSIC_PAGE_TYPE_ALBUM").FirstOrDefault();
        var durationMs = ParseDurationMs(FirstRunText(item, "lengthText"));
        var canonical = MediaRef.FromExternal(Provider, mediaRef.Id, MediaType.Music, MediaKind.Track);
        var artwork = LargestThumbnail(item);
        return new MediaDetailDto
        {
            Title = title,
            Subtitle = artist,
            Overview = album.Name is null ? Describe("Song", artist) : $"Song from {album.Name} by {artist}",
            PosterUrl = artwork,
            BackdropUrl = artwork,
            Year = FindYear(byline),
            Runtime = durationMs is > 0 ? Math.Max(1, (int)Math.Ceiling(durationMs.Value / 60000d)) : null,
            MediaType = MediaType.Music,
            ExternalId = canonical.Id,
            ExternalSource = Provider,
            MediaRef = canonical,
            Music = new MusicMetadataDto
            {
                Kind = MediaKind.Track,
                ArtistId = artists.Select(x => x.Id).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                ArtistCredit = artist,
                ReleaseGroupId = album.Id,
                AlbumTitle = album.Name,
                TrackCount = 1,
                Tracks =
                [
                    new MusicTrackMetadataDto
                    {
                        RecordingId = canonical.Id,
                        Title = title,
                        ArtistCredit = artist,
                        DiscNumber = 1,
                        TrackNumber = 1,
                        DurationMs = durationMs
                    }
                ]
            }
        };
    }

    private async Task<JsonDocument?> PostJsonAsync(string path, Dictionary<string, object?> body,
        string cacheKey, TimeSpan freshFor, CancellationToken cancellationToken = default)
    {
        cache.TryGetValue<CachedResponse>(cacheKey, out var cached);
        if (cached is not null && cached.FreshUntil > DateTime.UtcNow)
            return Parse(cached.Json, path);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var visitor = await EnsureVisitorAsync(cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Post, path);
                request.Headers.Referrer = new Uri("https://music.youtube.com/");
                request.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
                request.Headers.TryAddWithoutValidation("Cookie", "SOCS=CAI");
                if (!string.IsNullOrWhiteSpace(visitor))
                    request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitor);
                var payload = new Dictionary<string, object?>(body)
                {
                    ["context"] = new Dictionary<string, object?>
                    {
                        ["client"] = new Dictionary<string, object?>
                        {
                            ["clientName"] = "WEB_REMIX",
                            ["clientVersion"] = $"1.{DateTime.UtcNow:yyyyMMdd}.01.00",
                            ["hl"] = "en",
                            ["gl"] = "US"
                        },
                        ["user"] = new Dictionary<string, object?>()
                    }
                };
                request.Content = JsonContent.Create(payload);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    var parsed = Parse(json, path);
                    if (parsed is null) return null;
                    cache.Set(cacheKey, new CachedResponse(json, DateTime.UtcNow.Add(freshFor)), StaleFor);
                    return parsed;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    _visitorId = null;
                if (!IsTransient(response.StatusCode))
                {
                    logger.LogWarning("YouTube Music returned HTTP {Status} for {Path}",
                        (int)response.StatusCode, path.Split('?', 2)[0]);
                    break;
                }
                if (attempt < 2)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt + 1);
                    await Task.Delay(delay > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : delay,
                        cancellationToken);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                       && ex is HttpRequestException or TaskCanceledException)
            {
                if (attempt < 2)
                {
                    logger.LogDebug(ex, "Transient YouTube Music failure on attempt {Attempt}", attempt + 1);
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken);
                }
                else logger.LogWarning(ex, "YouTube Music request exhausted retries for {Path}", path.Split('?', 2)[0]);
            }
        }

        if (cached is not null)
        {
            logger.LogWarning("Serving stale YouTube Music metadata for {CacheKey} after an upstream failure", cacheKey);
            return Parse(cached.Json, path);
        }
        return null;
    }

    private async Task<string?> EnsureVisitorAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_visitorId)) return _visitorId;
        await _visitorLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_visitorId)) return _visitorId;
            using var request = new HttpRequestMessage(HttpMethod.Get, "");
            request.Headers.TryAddWithoutValidation("Cookie", "SOCS=CAI");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            _visitorId = VisitorDataRegex().Match(html) is { Success: true } match
                ? Regex.Unescape(match.Groups[1].Value)
                : null;
            return _visitorId;
        }
        finally { _visitorLock.Release(); }
    }

    private JsonDocument? Parse(string json, string path)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "YouTube Music returned invalid JSON for {Path}", path.Split('?', 2)[0]);
            return null;
        }
    }

    private static List<MediaCardDto> ParseSearch(JsonElement root, MediaKind kind)
        => FindObjects(root, "musicResponsiveListItemRenderer").Select(x => MapSearchItem(x, kind))
            .Where(x => x is not null).Cast<MediaCardDto>().ToList();

    private static MediaCardDto? MapSearchItem(JsonElement item, MediaKind kind)
    {
        var title = FlexColumnRuns(item, 0).Select(RunText).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var metadataRuns = FlexColumnRuns(item, 1);
        var artwork = LargestThumbnail(item);
        string? id;
        string? subtitle;
        int? year = FindYear(metadataRuns);
        int? runtime = null;
        switch (kind)
        {
            case MediaKind.Artist:
                id = FindBrowseId(item, "MUSIC_PAGE_TYPE_ARTIST");
                subtitle = "Artist · YouTube Music";
                break;
            case MediaKind.Track:
                id = FindFirstString(item, "videoId");
                subtitle = JoinNames(LinkedNames(metadataRuns, "MUSIC_PAGE_TYPE_ARTIST").Select(x => x.Name));
                var duration = metadataRuns.Select(RunText).Select(ParseDurationMs).FirstOrDefault(x => x.HasValue);
                runtime = duration is > 0 ? Math.Max(1, (int)Math.Ceiling(duration.Value / 60000d)) : null;
                break;
            default:
                id = FindBrowseId(item, "MUSIC_PAGE_TYPE_ALBUM")
                     ?? FindFirstString(item, "browseId", x => x.StartsWith("MPRE", StringComparison.Ordinal));
                subtitle = JoinNames(LinkedNames(metadataRuns, "MUSIC_PAGE_TYPE_ARTIST").Select(x => x.Name));
                break;
        }
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return null;
        var mediaRef = MediaRef.FromExternal(Provider, id, MediaType.Music, kind);
        return new MediaCardDto
        {
            Title = title,
            Subtitle = subtitle,
            Overview = Describe(kind == MediaKind.Track ? "Song" : kind.ToString(),
                kind == MediaKind.Artist ? null : subtitle),
            PosterUrl = artwork,
            BackdropUrl = artwork,
            Year = year,
            Runtime = runtime,
            MediaType = MediaType.Music,
            ExternalId = mediaRef.Id,
            ExternalSource = Provider,
            MediaRef = mediaRef
        };
    }

    private static List<MusicTrackMetadataDto> ParseAlbumTracks(JsonElement shelf, string? fallbackArtist)
    {
        var tracks = new List<MusicTrackMetadataDto>();
        foreach (var item in FindObjects(shelf, "musicResponsiveListItemRenderer"))
        {
            var title = FlexColumnRuns(item, 0).Select(RunText).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            var videoId = FindFirstString(item, "videoId");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(videoId)) continue;
            var artists = JoinNames(LinkedNames(FlexColumnRuns(item, 1), "MUSIC_PAGE_TYPE_ARTIST")
                .Select(x => x.Name)) ?? fallbackArtist;
            var durationText = FixedColumnRuns(item, 0).Select(RunText)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            tracks.Add(new MusicTrackMetadataDto
            {
                RecordingId = videoId,
                Title = title,
                ArtistCredit = artists,
                DiscNumber = 1,
                TrackNumber = tracks.Count + 1,
                DurationMs = ParseDurationMs(durationText)
            });
        }
        return tracks;
    }

    private async Task<List<MediaCardDto>> ParseCompleteArtistAlbumsAsync(JsonElement root, string artistId,
        string? fallbackArtist)
    {
        var preview = ParseArtistAlbums(root, fallbackArtist);
        var endpoint = FindArtistAlbumEndpoint(root);
        if (endpoint is null) return preview;
        using var full = await PostJsonAsync(BrowsePath, new Dictionary<string, object?>
        {
            ["browseId"] = endpoint.Value.BrowseId,
            ["params"] = endpoint.Value.Params
        }, $"ytm:artist-albums:all:{artistId}:{endpoint.Value.Params.GetHashCode(StringComparison.Ordinal)}",
            DetailFreshFor);
        if (full is null) return preview;
        var complete = MapAlbumTiles(full.RootElement, fallbackArtist);
        return complete.Count >= preview.Count ? complete : preview;
    }

    private static List<MediaCardDto> ParseArtistAlbums(JsonElement root, string? fallbackArtist)
    {
        var rows = new List<MediaCardDto>();
        foreach (var carousel in FindObjects(root, "musicCarouselShelfRenderer"))
        {
            var heading = CarouselHeading(carousel);
            if (heading?.Contains("album", StringComparison.OrdinalIgnoreCase) != true) continue;
            rows.AddRange(MapAlbumTiles(carousel, fallbackArtist));
        }
        return Deduplicate(rows);
    }

    private static List<MediaCardDto> MapAlbumTiles(JsonElement root, string? fallbackArtist)
    {
        var rows = new List<MediaCardDto>();
        foreach (var item in FindObjects(root, "musicTwoRowItemRenderer"))
        {
            var id = FindBrowseId(item, "MUSIC_PAGE_TYPE_ALBUM")
                     ?? FindFirstString(item, "browseId", x => x.StartsWith("MPRE", StringComparison.Ordinal));
            var title = FirstRunText(item, "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
            var mediaRef = MediaRef.FromExternal(Provider, id, MediaType.Music, MediaKind.Album);
            var subtitleRuns = DirectRuns(item, "subtitle");
            rows.Add(new MediaCardDto
            {
                Title = title,
                Subtitle = fallbackArtist,
                PosterUrl = LargestThumbnail(item),
                Year = FindYear(subtitleRuns),
                MediaType = MediaType.Music,
                ExternalId = mediaRef.Id,
                ExternalSource = Provider,
                MediaRef = mediaRef
            });
        }
        return Deduplicate(rows);
    }

    private static JsonElement? FindCarousel(JsonElement root, string heading)
        => FindObjects(root, "musicCarouselShelfRenderer")
            .FirstOrDefault(x => CarouselHeading(x)?.Contains(heading, StringComparison.OrdinalIgnoreCase) == true)
            is { ValueKind: JsonValueKind.Object } shelf ? shelf : null;

    private static List<MediaCardDto> MapTrackRows(JsonElement root)
    {
        var rows = new List<MediaCardDto>();
        foreach (var item in FindObjects(root, "musicResponsiveListItemRenderer"))
        {
            string? id = null;
            // playlistItemData is an object, not a string; prefer its direct video id when present so a
            // menu's radio/related endpoint can never become the identity of the visible row.
            if (item.TryGetProperty("playlistItemData", out var playlistData))
                id = DirectString(playlistData, "videoId");
            id ??= FindFirstString(item, "videoId");
            var title = FlexColumnRuns(item, 0).Select(RunText)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
            var metadata = FlexColumnRuns(item, 1);
            var artist = JoinNames(LinkedNames(metadata, "MUSIC_PAGE_TYPE_ARTIST").Select(x => x.Name))
                         ?? metadata.Select(RunText).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)
                             && x != " • ");
            var mediaRef = MediaRef.FromExternal(Provider, id, MediaType.Music, MediaKind.Track);
            rows.Add(new MediaCardDto
            {
                Title = title,
                Subtitle = artist,
                Overview = Describe("Song", artist),
                PosterUrl = LargestThumbnail(item),
                BackdropUrl = LargestThumbnail(item),
                MediaType = MediaType.Music,
                ExternalId = mediaRef.Id,
                ExternalSource = Provider,
                MediaRef = mediaRef
            });
        }
        return Deduplicate(rows);
    }

    private static List<MediaCardDto> MapArtistRows(JsonElement root)
    {
        var rows = new List<MediaCardDto>();
        foreach (var item in FindObjects(root, "musicResponsiveListItemRenderer"))
        {
            var id = FindBrowseId(item, "MUSIC_PAGE_TYPE_ARTIST");
            var title = FlexColumnRuns(item, 0).Select(RunText)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
            var mediaRef = MediaRef.FromExternal(Provider, id, MediaType.Music, MediaKind.Artist);
            rows.Add(new MediaCardDto
            {
                Title = title,
                Subtitle = JoinRuns(FlexColumnRuns(item, 1)) ?? "Artist · YouTube Music",
                Overview = "Artist on YouTube Music",
                PosterUrl = LargestThumbnail(item),
                BackdropUrl = LargestThumbnail(item),
                MediaType = MediaType.Music,
                ExternalId = mediaRef.Id,
                ExternalSource = Provider,
                MediaRef = mediaRef
            });
        }
        return Deduplicate(rows);
    }

    private static List<MusicPlaylistSummaryDto> MapPlaylistTiles(JsonElement root)
    {
        var rows = new List<MusicPlaylistSummaryDto>();
        foreach (var item in FindObjects(root, "musicTwoRowItemRenderer"))
        {
            var browseId = FindFirstString(item, "browseId", x => x.StartsWith("VL", StringComparison.Ordinal));
            var id = browseId is not null ? NormalizePlaylistId(browseId) :
                FindFirstString(item, "playlistId", x => !x.StartsWith("RDAMP", StringComparison.Ordinal));
            var title = FirstRunText(item, "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
            rows.Add(new MusicPlaylistSummaryDto
            {
                Id = id,
                Title = title,
                Subtitle = JoinRuns(DirectRuns(item, "subtitle")),
                ArtworkUrl = LargestThumbnail(item)
            });
        }
        return DeduplicatePlaylists(rows);
    }

    private static List<MusicPlaylistSummaryDto> DeduplicatePlaylists(
        IEnumerable<MusicPlaylistSummaryDto> rows)
        => rows.GroupBy(x => x.Id, StringComparer.Ordinal).Select(x => x.First()).ToList();

    private static List<MusicCategoryDto> ParseCategories(JsonElement root)
    {
        var categories = new List<MusicCategoryDto>();
        foreach (var grid in FindObjects(root, "gridRenderer"))
        {
            var group = "Moods & genres";
            if (grid.TryGetProperty("header", out var header)
                && TryFindObject(header, "gridHeaderRenderer", out var gridHeader))
                group = FirstRunText(gridHeader, "title") ?? group;
            if (!grid.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
                AddCategory(item, group, categories);
        }
        // The compact Explore shelf is not a grid on every YouTube layout.
        if (categories.Count == 0)
            foreach (var button in FindObjects(root, "musicNavigationButtonRenderer"))
                AddCategory(button, "Moods & genres", categories);
        return categories.Where(x => !string.IsNullOrWhiteSpace(x.Token))
            .GroupBy(x => x.Token, StringComparer.Ordinal).Select(x => x.First()).ToList();
    }

    private static void AddCategory(JsonElement root, string group, ICollection<MusicCategoryDto> categories)
    {
        if (!TryFindObject(root, "musicNavigationButtonRenderer", out var button)) button = root;
        var title = FirstRunText(button, "buttonText");
        var token = FindFirstString(button, "params");
        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(token))
            categories.Add(new MusicCategoryDto { Title = title, Group = group, Token = token });
    }

    private static string NormalizePlaylistId(string id)
        => id.StartsWith("VL", StringComparison.Ordinal) ? id[2..] : id;

    private static List<MediaCardDto> Page(List<MediaCardDto> rows, int page, int pageSize)
    {
        var take = Math.Clamp(pageSize, 1, 100);
        return rows.Skip((Math.Max(1, page) - 1) * take).Take(take).ToList();
    }

    private static List<MediaCardDto> Deduplicate(IEnumerable<MediaCardDto> rows)
        => rows.GroupBy(x => x.ResolveMediaRef().StableKey, StringComparer.Ordinal)
            .Select(x => x.First()).ToList();

    private static (string BrowseId, string Params)? FindArtistAlbumEndpoint(JsonElement root)
    {
        foreach (var carousel in FindObjects(root, "musicCarouselShelfRenderer"))
        {
            if (CarouselHeading(carousel)?.Contains("album", StringComparison.OrdinalIgnoreCase) != true) continue;
            if (!TryFindObject(carousel, "musicCarouselShelfBasicHeaderRenderer", out var header)) continue;
            foreach (var endpoint in FindObjects(header, "browseEndpoint"))
            {
                var browseId = DirectString(endpoint, "browseId");
                var parameters = DirectString(endpoint, "params");
                if (!string.IsNullOrWhiteSpace(browseId) && !string.IsNullOrWhiteSpace(parameters))
                    return (browseId, parameters);
            }
        }
        return null;
    }

    private static string? CarouselHeading(JsonElement carousel)
        => TryFindObject(carousel, "musicCarouselShelfBasicHeaderRenderer", out var header)
            ? FirstRunText(header, "title")
            : null;

    private static List<JsonElement> FlexColumnRuns(JsonElement item, int index)
    {
        if (!item.TryGetProperty("flexColumns", out var columns) || columns.ValueKind != JsonValueKind.Array
            || index < 0 || index >= columns.GetArrayLength()) return new();
        var column = columns[index];
        return column.TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var renderer)
            ? DirectRuns(renderer, "text") : new();
    }

    private static List<JsonElement> FixedColumnRuns(JsonElement item, int index)
    {
        if (!item.TryGetProperty("fixedColumns", out var columns) || columns.ValueKind != JsonValueKind.Array
            || index < 0 || index >= columns.GetArrayLength()) return new();
        var column = columns[index];
        return column.TryGetProperty("musicResponsiveListItemFixedColumnRenderer", out var renderer)
            ? DirectRuns(renderer, "text") : new();
    }

    private static List<JsonElement> DirectRuns(JsonElement owner, string property)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(property, out var text)
            || text.ValueKind != JsonValueKind.Object || !text.TryGetProperty("runs", out var runs)
            || runs.ValueKind != JsonValueKind.Array) return new();
        return runs.EnumerateArray().ToList();
    }

    private static string? FirstRunText(JsonElement owner, string property)
        => DirectRuns(owner, property).Select(RunText).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    private static string? RunText(JsonElement run) => DirectString(run, "text");

    private static string? DirectString(JsonElement owner, string property)
        => owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<(string Name, string? Id)> LinkedNames(IEnumerable<JsonElement> runs, string pageType)
    {
        var result = new List<(string Name, string? Id)>();
        foreach (var run in runs)
        {
            var text = RunText(run);
            if (string.IsNullOrWhiteSpace(text) || !TryFindObject(run, "browseEndpoint", out var endpoint)) continue;
            var type = FindFirstString(endpoint, "pageType");
            if (!string.Equals(type, pageType, StringComparison.Ordinal)) continue;
            result.Add((text, DirectString(endpoint, "browseId")));
        }
        return result;
    }

    private static string? FindBrowseId(JsonElement root, string pageType)
    {
        foreach (var endpoint in FindObjects(root, "browseEndpoint"))
            if (string.Equals(FindFirstString(endpoint, "pageType"), pageType, StringComparison.Ordinal))
            {
                var id = DirectString(endpoint, "browseId");
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }
        return null;
    }

    private static string? LargestThumbnail(JsonElement root)
    {
        string? selected = null;
        long selectedArea = -1;
        foreach (var array in FindPropertyValues(root, "thumbnails").Where(x => x.ValueKind == JsonValueKind.Array))
            foreach (var image in array.EnumerateArray())
            {
                var url = DirectString(image, "url");
                if (string.IsNullOrWhiteSpace(url)) continue;
                var width = DirectInt(image, "width") ?? 0;
                var height = DirectInt(image, "height") ?? 0;
                var area = (long)width * height;
                if (selected is null || area > selectedArea) { selected = url; selectedArea = area; }
            }
        return selected;
    }

    private static int? DirectInt(JsonElement owner, string property)
        => owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static int? FindYear(IEnumerable<JsonElement> runs)
        => runs.Select(RunText).Select(x => x is { Length: 4 }
                && int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : (int?)null)
            .FirstOrDefault(x => x is >= 1000 and <= 9999);

    private static int? ParseDurationMs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !DurationRegex().IsMatch(value)) return null;
        var parts = value.Split(':');
        long seconds = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return null;
            seconds = seconds * 60 + number;
        }
        return seconds is > 0 and <= int.MaxValue / 1000 ? (int)(seconds * 1000) : null;
    }

    private static string? FindContinuation(JsonElement root)
    {
        foreach (var value in FindPropertyValues(root, "nextContinuationData"))
            if (value.ValueKind == JsonValueKind.Object)
            {
                var token = DirectString(value, "continuation");
                if (!string.IsNullOrWhiteSpace(token)) return token;
            }
        return null;
    }

    private static string? FindFirstString(JsonElement root, string property, Func<string, bool>? predicate = null)
        => FindPropertyValues(root, property).Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()).Where(x => x is not null && (predicate is null || predicate(x)))
            .FirstOrDefault();

    private static IEnumerable<JsonElement> FindObjects(JsonElement root, string property)
        => FindPropertyValues(root, property).Where(x => x.ValueKind == JsonValueKind.Object);

    private static bool TryFindObject(JsonElement root, string property, out JsonElement value)
    {
        value = FindObjects(root, property).FirstOrDefault();
        return value.ValueKind == JsonValueKind.Object;
    }

    private static IEnumerable<JsonElement> FindPropertyValues(JsonElement root, string property)
    {
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var item in root.EnumerateObject())
            {
                if (item.NameEquals(property)) yield return item.Value;
                foreach (var child in FindPropertyValues(item.Value, property)) yield return child;
            }
        else if (root.ValueKind == JsonValueKind.Array)
            foreach (var item in root.EnumerateArray())
                foreach (var child in FindPropertyValues(item, property)) yield return child;
    }

    private static string? JoinRuns(IEnumerable<JsonElement> runs)
    {
        var text = string.Concat(runs.Select(RunText));
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? JoinNames(IEnumerable<string?> names)
    {
        var value = string.Join(" & ", names.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? Describe(string? type, string? artist)
        => !string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(artist)
            ? $"{type} by {artist}"
            : type ?? artist;

    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private sealed record CachedResponse(string Json, DateTime FreshUntil);

    [GeneratedRegex("\\\"VISITOR_DATA\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex VisitorDataRegex();

    [GeneratedRegex("^(?:\\d+:){1,2}\\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationRegex();
}
