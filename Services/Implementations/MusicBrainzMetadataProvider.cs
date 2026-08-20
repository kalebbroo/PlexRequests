using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>MusicBrainz metadata with Cover Art Archive artwork. All calls share the process-wide
/// MusicBrainz rate gate, transient failures are retried, and parsed results are cached so UI searches
/// do not turn into API traffic on every keystroke.</summary>
public sealed class MusicBrainzMetadataProvider(
    HttpClient http,
    IMemoryCache cache,
    IMusicBrainzRateGate rateGate,
    IListenBrainzDiscoveryClient discovery,
    ILogger<MusicBrainzMetadataProvider> logger) : IMediaMetadataProvider
{
    private const string Provider = "musicbrainz";
    private static readonly TimeSpan SearchTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DetailTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan DiscoveryTtl = TimeSpan.FromHours(6);
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true };

    public string ProviderKey => Provider;
    public bool RequiresApiKey => false;
    public bool IsAvailable => true;
    public bool Supports(MediaType mediaType) => mediaType == MediaType.Music;

    public Task<List<MediaCardDto>> SearchAsync(string query, MediaType? mediaType = null, int page = 1,
        int pageSize = 20) => SearchAsync(new MediaSearchQuery
        {
            Query = query, MediaType = mediaType ?? MediaType.Music, Kind = MediaKind.Album,
            Page = page, PageSize = pageSize
        });

    public async Task<List<MediaCardDto>> SearchAsync(MediaSearchQuery query)
    {
        if (query.MediaType is not null && query.MediaType != MediaType.Music) return new();
        if (string.IsNullOrWhiteSpace(query.Query)) return new();
        var kind = query.Kind is MediaKind.Artist or MediaKind.Track ? query.Kind.Value : MediaKind.Album;
        var page = Math.Max(1, query.Page);
        var take = Math.Clamp(query.PageSize, 1, 100);
        var offset = (page - 1) * take;
        var entity = kind switch
        {
            MediaKind.Artist => "artist",
            MediaKind.Track => "recording",
            _ => "release-group"
        };
        var searchText = kind == MediaKind.Album
            ? $"(releasegroup:\"{EscapeLucene(query.Query.Trim())}\" OR artist:\"{EscapeLucene(query.Query.Trim())}\") AND primarytype:album"
            : query.Query.Trim();
        var url = BuildUrl($"ws/2/{entity}", ("query", searchText), ("fmt", "json"),
            ("limit", take.ToString(CultureInfo.InvariantCulture)),
            ("offset", offset.ToString(CultureInfo.InvariantCulture)));
        using var doc = await GetJsonAsync(url, $"mb:search:{kind}:{page}:{take}:{query.Query.Trim().ToLowerInvariant()}", SearchTtl);
        return doc is null ? new() : kind switch
        {
            MediaKind.Artist => MapArray(doc.RootElement, "artists", MapArtist),
            MediaKind.Track => MapArray(doc.RootElement, "recordings", MapRecording),
            _ => MapArray(doc.RootElement, "release-groups", MapReleaseGroup)
        };
    }

    public Task<MediaDetailDto?> GetDetailsAsync(int mediaId, MediaType mediaType)
        => Task.FromResult<MediaDetailDto?>(null);

    public async Task<MediaDetailDto?> GetDetailsAsync(MediaRef mediaRef)
    {
        if (!mediaRef.IsValid || mediaRef.MediaType != MediaType.Music
            || !mediaRef.Provider.Equals(Provider, StringComparison.OrdinalIgnoreCase)) return null;
        return mediaRef.Kind switch
        {
            MediaKind.Artist => await GetArtistAsync(mediaRef),
            MediaKind.Track => await GetRecordingAsync(mediaRef),
            MediaKind.Album => await GetReleaseGroupAsync(mediaRef),
            _ => null
        };
    }

    public Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10)
        => DiscoverRecentAsync(1, count);

    public Task<List<MediaCardDto>> GetLibraryAsync(MediaType mediaType, int page = 1, int pageSize = 20)
        => mediaType == MediaType.Music ? DiscoverRecentAsync(page, pageSize) : Task.FromResult(new List<MediaCardDto>());

    public async Task<List<MediaCardDto>> GetTrendingAsync(MediaType? mediaType = null, int page = 1, int pageSize = 20)
    {
        if (mediaType is not null and not MediaType.Music) return new();
        var trending = await discovery.GetTrendingAlbumsAsync(page, pageSize);
        return trending.Count > 0 ? trending : await DiscoverRecentAsync(page, pageSize);
    }

    public Task<List<MediaCardDto>> GetPopularAsync(MediaType mediaType, int page = 1, int pageSize = 20)
        => mediaType == MediaType.Music
            ? GetTrendingAsync(mediaType, page, pageSize)
            : Task.FromResult(new List<MediaCardDto>());

    public Task<List<MediaCardDto>> GetTopRatedAsync(MediaType mediaType, int page = 1, int pageSize = 20)
        => GetPopularAsync(mediaType, page, pageSize);

    public async Task<List<MediaCardDto>> GetByGenreAsync(MediaType mediaType, string genre, int page = 1,
        int pageSize = 20)
    {
        if (mediaType != MediaType.Music || string.IsNullOrWhiteSpace(genre)) return new();
        return await SearchReleaseGroupsAsync($"tag:\"{EscapeLucene(genre)}\" AND primarytype:album", page,
            pageSize, $"genre:{genre.ToLowerInvariant()}", DiscoveryTtl);
    }

    public Task<string?> GetImdbIdAsync(int mediaId, MediaType mediaType) => Task.FromResult<string?>(null);
    public Task<string?> GetImdbIdAsync(MediaRef mediaRef) => Task.FromResult<string?>(null);

    public async Task<List<MediaCardDto>> GetSimilarAsync(MediaRef mediaRef, int count = 12)
    {
        var detail = await GetDetailsAsync(mediaRef);
        var artistId = detail?.Music?.ArtistId;
        if (string.IsNullOrWhiteSpace(artistId)) return new();
        var items = await SearchReleaseGroupsAsync($"arid:{artistId} AND primarytype:album", 1, count + 1,
            $"artist:{artistId}", DetailTtl);
        return items.Where(x => x.ResolveMediaRef().StableKey != mediaRef.StableKey).Take(count).ToList();
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        using var doc = await GetJsonAsync("ws/2/artist/056e4f3e-d505-4dad-8ec1-d04f521cbb56?fmt=json",
            "mb:health", TimeSpan.FromMinutes(1), cancellationToken);
        return doc?.RootElement.TryGetProperty("id", out _) == true;
    }

    private async Task<List<MediaCardDto>> DiscoverRecentAsync(int page, int pageSize)
    {
        var end = DateTime.UtcNow.Date;
        var start = end.AddDays(-45);
        var query = $"firstreleasedate:[{start:yyyy-MM-dd} TO {end:yyyy-MM-dd}] AND primarytype:album";
        return await SearchReleaseGroupsAsync(query, page, pageSize, $"recent:{start:yyyyMMdd}:{end:yyyyMMdd}",
            DiscoveryTtl);
    }

    private async Task<List<MediaCardDto>> SearchReleaseGroupsAsync(string query, int page, int pageSize,
        string cacheKey, TimeSpan ttl)
    {
        var take = Math.Clamp(pageSize, 1, 100);
        var safePage = Math.Max(1, page);
        var url = BuildUrl("ws/2/release-group", ("query", query), ("fmt", "json"),
            ("limit", take.ToString(CultureInfo.InvariantCulture)),
            ("offset", ((safePage - 1) * take).ToString(CultureInfo.InvariantCulture)));
        using var doc = await GetJsonAsync(url, $"mb:rg:{cacheKey}:{safePage}:{take}", ttl);
        return doc is null ? new() : MapArray(doc.RootElement, "release-groups", MapReleaseGroup);
    }

    private async Task<MediaDetailDto?> GetReleaseGroupAsync(MediaRef mediaRef)
    {
        var url = BuildUrl($"ws/2/release-group/{Uri.EscapeDataString(mediaRef.Id)}",
            ("inc", "artists+releases+tags+genres+ratings"), ("fmt", "json"));
        using var doc = await GetJsonAsync(url, $"mb:detail:album:{mediaRef.Id}", DetailTtl);
        if (doc is null) return null;
        var card = MapReleaseGroup(doc.RootElement);
        if (card is null) return null;
        var detail = ToDetail(card);
        var release = SelectRelease(doc.RootElement);
        var artist = ArtistCredit(doc.RootElement);
        var artistId = FirstArtistId(doc.RootElement);
        var primaryType = Text(doc.RootElement, "primary-type");
        detail.Overview = Describe(primaryType, artist, StringArray(doc.RootElement, "secondary-types"));
        detail.Music = new MusicMetadataDto
        {
            Kind = MediaKind.Album,
            ArtistId = artistId,
            ArtistCredit = artist,
            PrimaryType = primaryType,
            SecondaryTypes = StringArray(doc.RootElement, "secondary-types"),
            ReleaseGroupId = mediaRef.Id,
            ReleaseId = release
        };
        if (!string.IsNullOrWhiteSpace(release))
        {
            using var releaseDoc = await GetJsonAsync(BuildUrl($"ws/2/release/{Uri.EscapeDataString(release)}",
                    ("inc", "recordings+artist-credits+media+labels"), ("fmt", "json")),
                $"mb:release:{release}", DetailTtl);
            if (releaseDoc is not null)
            {
                detail.Music.Tracks = MapTracks(releaseDoc.RootElement);
                detail.Music.TrackCount = detail.Music.Tracks.Count;
                detail.Studio = FirstLabel(releaseDoc.RootElement);
            }
        }
        return detail;
    }

    private async Task<MediaDetailDto?> GetArtistAsync(MediaRef mediaRef)
    {
        var url = BuildUrl($"ws/2/artist/{Uri.EscapeDataString(mediaRef.Id)}",
            ("inc", "release-groups+tags+genres+ratings"), ("fmt", "json"));
        using var doc = await GetJsonAsync(url, $"mb:detail:artist:{mediaRef.Id}", DetailTtl);
        if (doc is null) return null;
        var card = MapArtist(doc.RootElement);
        if (card is null) return null;
        var detail = ToDetail(card);
        detail.Overview = Text(doc.RootElement, "disambiguation");
        detail.Status = Text(doc.RootElement, "type");
        var country = Text(doc.RootElement, "country");
        if (!string.IsNullOrWhiteSpace(country)) detail.Countries.Add(country);
        detail.Music = new MusicMetadataDto
        {
            Kind = MediaKind.Artist,
            ArtistId = mediaRef.Id,
            ArtistCredit = card.Title,
            AlbumTitles = doc.RootElement.TryGetProperty("release-groups", out var groups)
                          && groups.ValueKind == JsonValueKind.Array
                ? groups.EnumerateArray().Select(x => Text(x, "title"))
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : new()
        };
        return detail;
    }

    private async Task<MediaDetailDto?> GetRecordingAsync(MediaRef mediaRef)
    {
        var url = BuildUrl($"ws/2/recording/{Uri.EscapeDataString(mediaRef.Id)}",
            ("inc", "artist-credits+releases+release-groups+tags+genres"), ("fmt", "json"));
        using var doc = await GetJsonAsync(url, $"mb:detail:track:{mediaRef.Id}", DetailTtl);
        if (doc is null) return null;
        var card = MapRecording(doc.RootElement);
        if (card is null) return null;
        var detail = ToDetail(card);
        var releaseGroupId = FirstReleaseGroupId(doc.RootElement);
        detail.PosterUrl = CoverArt(releaseGroupId);
        detail.Music = new MusicMetadataDto
        {
            Kind = MediaKind.Track,
            ArtistId = FirstArtistId(doc.RootElement),
            ArtistCredit = ArtistCredit(doc.RootElement),
            ReleaseGroupId = releaseGroupId,
            AlbumTitle = FirstReleaseTitle(doc.RootElement),
            TrackCount = 1,
            Tracks =
            [
                new MusicTrackMetadataDto
                {
                    RecordingId = mediaRef.Id,
                    Title = detail.Title,
                    ArtistCredit = ArtistCredit(doc.RootElement),
                    TrackNumber = 1,
                    DiscNumber = 1,
                    DurationMs = Number(doc.RootElement, "length")
                }
            ]
        };
        return detail;
    }

    private async Task<JsonDocument?> GetJsonAsync(string relativeUrl, string cacheKey, TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue<string>(cacheKey, out var cached) && !string.IsNullOrWhiteSpace(cached))
            return JsonDocument.Parse(cached, JsonOptions);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await rateGate.WaitAsync(cancellationToken);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    try { using var validation = JsonDocument.Parse(json, JsonOptions); }
                    catch (JsonException ex)
                    {
                        logger.LogWarning(ex, "MusicBrainz returned invalid JSON for {Url}", relativeUrl);
                        return null;
                    }
                    cache.Set(cacheKey, json, ttl);
                    return JsonDocument.Parse(json, JsonOptions);
                }
                if (!IsTransient(response.StatusCode))
                {
                    logger.LogWarning("MusicBrainz returned HTTP {Status} for {Url}", (int)response.StatusCode,
                        relativeUrl);
                    return null;
                }
                if (attempt < 2)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt + 1);
                    await Task.Delay(retryAfter > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : retryAfter,
                        cancellationToken);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                       && ex is HttpRequestException or TaskCanceledException)
            {
                if (attempt < 2)
                    logger.LogDebug(ex, "Transient MusicBrainz call failure on attempt {Attempt}", attempt + 1);
                else
                    logger.LogWarning(ex, "MusicBrainz request exhausted retries for {Url}", relativeUrl);
            }
        }
        logger.LogWarning("MusicBrainz request exhausted retries for {Url}", relativeUrl);
        return null;
    }

    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.TooManyRequests
        or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static string BuildUrl(string path, params (string Key, string Value)[] query)
        => $"{path}?{string.Join('&', query.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"))}";

    private static List<MediaCardDto> MapArray(JsonElement root, string property,
        Func<JsonElement, MediaCardDto?> map)
    {
        if (!root.TryGetProperty(property, out var rows) || rows.ValueKind != JsonValueKind.Array) return new();
        return rows.EnumerateArray().Select(map).Where(x => x is not null).Cast<MediaCardDto>().ToList();
    }

    private static MediaCardDto? MapReleaseGroup(JsonElement row)
    {
        var id = Text(row, "id");
        var title = Text(row, "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return null;
        var mediaRef = MediaRef.FromExternal(Provider, id, MediaType.Music, MediaKind.Album);
        return new MediaCardDto
        {
            Title = title,
            Subtitle = ArtistCredit(row),
            MediaType = MediaType.Music,
            ExternalId = mediaRef.Id,
            ExternalSource = Provider,
            MediaRef = mediaRef,
            PosterUrl = CoverArt(id),
            Year = Year(Text(row, "first-release-date")),
            Rating = Rating(row),
            Genres = Tags(row),
            Overview = Describe(Text(row, "primary-type"), ArtistCredit(row),
                StringArray(row, "secondary-types"))
        };
    }

    private static MediaCardDto? MapArtist(JsonElement row)
    {
        var id = Text(row, "id");
        var name = Text(row, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return null;
        var mediaRef = MediaRef.FromExternal(Provider, id, MediaType.Music, MediaKind.Artist);
        return new MediaCardDto
        {
            Title = name,
            Subtitle = Text(row, "disambiguation"),
            Overview = Text(row, "disambiguation"),
            MediaType = MediaType.Music,
            ExternalId = mediaRef.Id,
            ExternalSource = Provider,
            MediaRef = mediaRef,
            Rating = Rating(row),
            Genres = Tags(row)
        };
    }

    private static MediaCardDto? MapRecording(JsonElement row)
    {
        var id = Text(row, "id");
        var title = Text(row, "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return null;
        var mediaRef = MediaRef.FromExternal(Provider, id, MediaType.Music, MediaKind.Track);
        var releaseGroup = FirstReleaseGroupId(row);
        return new MediaCardDto
        {
            Title = title,
            Subtitle = ArtistCredit(row),
            MediaType = MediaType.Music,
            ExternalId = mediaRef.Id,
            ExternalSource = Provider,
            MediaRef = mediaRef,
            PosterUrl = CoverArt(releaseGroup),
            Runtime = Number(row, "length") is int milliseconds ? Math.Max(1, milliseconds / 60000) : null,
            Genres = Tags(row)
        };
    }

    private static MediaDetailDto ToDetail(MediaCardDto card) => new()
    {
        Id = card.Id,
        Title = card.Title,
        Subtitle = card.Subtitle,
        Overview = card.Overview,
        PosterUrl = card.PosterUrl,
        BackdropUrl = card.BackdropUrl,
        Year = card.Year,
        Rating = card.Rating,
        Runtime = card.Runtime,
        MediaType = card.MediaType,
        Genres = card.Genres,
        ExternalId = card.ExternalId,
        ExternalSource = card.ExternalSource,
        MediaRef = card.MediaRef
    };

    private static List<MusicTrackMetadataDto> MapTracks(JsonElement release)
    {
        var tracks = new List<MusicTrackMetadataDto>();
        if (!release.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array) return tracks;
        var fallbackDisc = 0;
        foreach (var medium in media.EnumerateArray())
        {
            fallbackDisc++;
            var disc = Number(medium, "position") ?? fallbackDisc;
            if (!medium.TryGetProperty("tracks", out var rows) || rows.ValueKind != JsonValueKind.Array) continue;
            var fallbackTrack = 0;
            foreach (var track in rows.EnumerateArray())
            {
                fallbackTrack++;
                var recording = track.TryGetProperty("recording", out var value) ? value : track;
                tracks.Add(new MusicTrackMetadataDto
                {
                    RecordingId = Text(recording, "id") ?? string.Empty,
                    Title = Text(track, "title") ?? Text(recording, "title") ?? "Unknown track",
                    ArtistCredit = ArtistCredit(track) ?? ArtistCredit(recording),
                    DiscNumber = disc,
                    TrackNumber = Number(track, "position") ?? fallbackTrack,
                    DurationMs = Number(track, "length") ?? Number(recording, "length")
                });
            }
        }
        return tracks;
    }

    private static string? SelectRelease(JsonElement row)
    {
        if (!row.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) return null;
        return releases.EnumerateArray()
            .OrderByDescending(x => string.Equals(Text(x, "status"), "Official", StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => Text(x, "date") ?? "9999")
            .Select(x => Text(x, "id"))
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    }

    private static string? FirstReleaseGroupId(JsonElement row)
    {
        if (!row.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) return null;
        foreach (var release in releases.EnumerateArray())
            if (release.TryGetProperty("release-group", out var group))
            {
                var id = Text(group, "id");
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }
        return null;
    }

    private static string? FirstReleaseTitle(JsonElement row)
    {
        if (!row.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
            return null;
        return releases.EnumerateArray()
            .OrderByDescending(x => string.Equals(Text(x, "status"), "Official", StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => Text(x, "date") ?? "9999")
            .Select(x => Text(x, "title"))
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    }

    private static string? FirstLabel(JsonElement row)
    {
        if (!row.TryGetProperty("label-info", out var labels) || labels.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in labels.EnumerateArray())
            if (item.TryGetProperty("label", out var label))
            {
                var name = Text(label, "name");
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        return null;
    }

    private static string? ArtistCredit(JsonElement row)
    {
        if (!row.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array) return null;
        var names = credits.EnumerateArray().Select(x => Text(x, "name")
            ?? (x.TryGetProperty("artist", out var artist) ? Text(artist, "name") : null))
            .Where(x => !string.IsNullOrWhiteSpace(x));
        var value = string.Join(" & ", names!);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? FirstArtistId(JsonElement row)
    {
        if (!row.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array) return null;
        foreach (var credit in credits.EnumerateArray())
            if (credit.TryGetProperty("artist", out var artist))
            {
                var id = Text(artist, "id");
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }
        return null;
    }

    private static List<string> Tags(JsonElement row)
    {
        var source = row.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array
            ? genres
            : row.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array ? tags : default;
        if (source.ValueKind != JsonValueKind.Array) return new();
        return source.EnumerateArray()
            .Select(x => new { Name = Text(x, "name"), Count = Number(x, "count") ?? 0 })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .OrderByDescending(x => x.Count)
            .Select(x => x.Name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    private static decimal? Rating(JsonElement row)
    {
        if (!row.TryGetProperty("rating", out var rating) || rating.ValueKind != JsonValueKind.Object) return null;
        if (!rating.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number) return null;
        return value.TryGetDecimal(out var number) ? number * 2 : null;
    }

    private static string? CoverArt(string? releaseGroupId) => string.IsNullOrWhiteSpace(releaseGroupId)
        ? null
        : $"https://coverartarchive.org/release-group/{Uri.EscapeDataString(releaseGroupId)}/front-500";

    private static string? Describe(string? primaryType, string? artist, IReadOnlyList<string> secondaryTypes)
    {
        var type = string.Join(" · ", new[] { primaryType }.Concat(secondaryTypes)
            .Where(x => !string.IsNullOrWhiteSpace(x))!);
        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(type)) return $"{type} by {artist}";
        return artist ?? (string.IsNullOrWhiteSpace(type) ? null : type);
    }

    private static string? Text(JsonElement row, string property)
        => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement row, string property)
        => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static List<string> StringArray(JsonElement row, string property)
        => row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList()
            : new();

    private static int? Year(string? date) => date is { Length: >= 4 }
        && int.TryParse(date[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string EscapeLucene(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}
