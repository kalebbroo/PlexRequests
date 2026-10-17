using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequestsHosted.Services.Implementations;

public sealed class YouTubeDataOptions
{
    public const string Section = "YouTubeData";
    /// <summary>Google API key with the YouTube Data API v3 enabled. Read-only public data. It is never stored in the
    /// database and never written to logs.</summary>
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://www.googleapis.com/youtube/v3/";
    public int MaxPagesPerChannel { get; set; } = 10;
    public int CacheHours { get; set; } = 6;
}

/// <summary>Reads official channel uploads through the YouTube Data API. Listing a channel's uploads costs about one
/// unit per 50 videos, while a search per episode costs far more. The whole channel is read once, cached, and matched
/// locally, so a season costs the same as an episode.</summary>
public sealed partial class YouTubeDataVideoCatalog(HttpClient http, IOptions<YouTubeDataOptions> options,
    IMemoryCache cache, ILogger<YouTubeDataVideoCatalog> logger) : IYouTubeVideoCatalog
{
    public static bool IsChannelId(string? value) => value is not null && ChannelIdPattern().IsMatch(value);

    public async Task<IReadOnlyList<YouTubeCatalogVideo>?> GetChannelUploadsAsync(string channelId,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || !IsChannelId(channelId)) return null;

        var cacheKey = $"ytv:uploads:{channelId}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<YouTubeCatalogVideo>? cached) && cached is not null)
            return cached;
        try
        {
            var uploads = await UploadsPlaylistAsync(channelId, cancellationToken);
            if (uploads is null) return null;
            var ids = await PlaylistVideoIdsAsync(uploads, settings, cancellationToken);
            var videos = await VideoDetailsAsync(ids, cancellationToken);
            cache.Set(cacheKey, (IReadOnlyList<YouTubeCatalogVideo>)videos,
                TimeSpan.FromHours(Math.Clamp(settings.CacheHours, 1, 24)));
            return videos;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Log the type only: exception text from the HTTP stack can carry the request URL, and the URL carries the key.
            logger.LogWarning("YouTube channel {ChannelId} could not be read ({Reason}); episodes will be retried later",
                channelId, ex.GetType().Name);
            return null;
        }
    }

    private async Task<string?> UploadsPlaylistAsync(string channelId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync("channels",
            [new("part", "contentDetails"), new("id", channelId)], ct);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0) return null;
        return items[0].GetProperty("contentDetails").GetProperty("relatedPlaylists").GetProperty("uploads").GetString();
    }

    private async Task<List<string>> PlaylistVideoIdsAsync(string playlistId, YouTubeDataOptions settings,
        CancellationToken ct)
    {
        var ids = new List<string>();
        string? pageToken = null;
        for (var page = 0; page < Math.Clamp(settings.MaxPagesPerChannel, 1, 20); page++)
        {
            var query = new List<KeyValuePair<string, string>>
            {
                new("part", "contentDetails"), new("playlistId", playlistId), new("maxResults", "50")
            };
            if (pageToken is not null) query.Add(new("pageToken", pageToken));
            using var doc = await GetJsonAsync("playlistItems", query, ct);
            if (doc.RootElement.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("contentDetails", out var details)
                        && details.TryGetProperty("videoId", out var id)
                        && id.GetString() is { } videoId)
                        ids.Add(videoId);
                }
            }
            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            if (pageToken is null) break;
        }
        return ids.Distinct(StringComparer.Ordinal).ToList();
    }

    private async Task<List<YouTubeCatalogVideo>> VideoDetailsAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var videos = new List<YouTubeCatalogVideo>();
        foreach (var batch in ids.Chunk(50))
        {
            using var doc = await GetJsonAsync("videos",
                [new("part", "contentDetails,snippet"), new("id", string.Join(',', batch))], ct);
            if (!doc.RootElement.TryGetProperty("items", out var items)) continue;
            foreach (var item in items.EnumerateArray())
            {
                var videoId = item.GetProperty("id").GetString();
                if (!DirectVideoLocator.IsVideoId(videoId)) continue;
                var snippet = item.GetProperty("snippet");
                var live = snippet.TryGetProperty("liveBroadcastContent", out var liveValue)
                    ? liveValue.GetString() : "none";
                videos.Add(new YouTubeCatalogVideo(videoId!,
                    snippet.GetProperty("channelId").GetString() ?? string.Empty,
                    snippet.GetProperty("title").GetString() ?? string.Empty,
                    ParseSeconds(item.GetProperty("contentDetails").GetProperty("duration").GetString()),
                    live is not "none"));
            }
        }
        return videos;
    }

    private async Task<JsonDocument> GetJsonAsync(string resource, IEnumerable<KeyValuePair<string, string>> query,
        CancellationToken ct)
    {
        var settings = options.Value;
        var parameters = query.Append(new KeyValuePair<string, string>("key", settings.ApiKey))
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}");
        var url = $"{settings.BaseUrl.TrimEnd('/')}/{resource}?{string.Join('&', parameters)}";
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"YouTube Data API {resource} returned HTTP {(int)response.StatusCode}");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static int ParseSeconds(string? isoDuration)
    {
        try { return (int)XmlConvert.ToTimeSpan(isoDuration ?? string.Empty).TotalSeconds; }
        catch (FormatException) { return 0; }
    }

    [GeneratedRegex("^UC[A-Za-z0-9_-]{22}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelIdPattern();
}
