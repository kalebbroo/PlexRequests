using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

public class PlexApiService : IPlexApiService
{
    private readonly IMediaMetadataProvider _metadata;
    private readonly HttpClient _http;
    private readonly PlexConfiguration _cfg;
    private readonly IMemoryCache _cache;

    public PlexApiService(IMediaMetadataProvider metadata, HttpClient httpClient, IOptions<PlexConfiguration> options, IMemoryCache cache)
    {
        _metadata = metadata;
        _http = httpClient;
        _cfg = options.Value;
        _cache = cache;
        EnsureDefaultHeaders(_http.DefaultRequestHeaders);
    }

    public async Task<PlexServerInfo?> GetServerInfoAsync()
    {
        if (string.IsNullOrWhiteSpace(_cfg.PrimaryServerUrl) || string.IsNullOrWhiteSpace(_cfg.ServerToken))
            return null;

        var baseUrl = NormalizeBaseUrl(_cfg.PrimaryServerUrl);
        if (baseUrl is null) return null;
        var url = baseUrl + "/";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureDefaultHeaders(req.Headers);
        req.Headers.Add("X-Plex-Token", _cfg.ServerToken);
        var res = await _http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return new PlexServerInfo { IsOnline = false };

        var text = await res.Content.ReadAsStringAsync();
        // The root endpoint returns XML normally; keep lightweight parse by sniffing version/name if present.
        var name = GetBetween(text, "friendlyName=\"", "\"") ?? "Plex Server";
        var version = GetBetween(text, "version=\"", "\"") ?? string.Empty;
        return new PlexServerInfo { Name = name, Version = version, IsOnline = true };
    }

    public async Task<List<PlexLibrary>> GetLibrariesAsync()
    {
        if (string.IsNullOrWhiteSpace(_cfg.PrimaryServerUrl) || string.IsNullOrWhiteSpace(_cfg.ServerToken))
            return new();

        var baseUrl = NormalizeBaseUrl(_cfg.PrimaryServerUrl);
        if (baseUrl is null) return new();
        var url = baseUrl + "/library/sections";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureDefaultHeaders(req.Headers);
        req.Headers.Add("X-Plex-Token", _cfg.ServerToken);
        // Ask for JSON where supported
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var res = await _http.SendAsync(req);
        if (!res.IsSuccessStatusCode)
            return new();

        var json = await res.Content.ReadAsStringAsync();
        var sections = JsonSerializer.Deserialize<PlexDirectoryContainer>(json, JsonOpts);
        if (sections?.MediaContainer?.Directories is null) return new();

        var list = new List<PlexLibrary>();
        foreach (var d in sections.MediaContainer.Directories)
        {
            var type = d.Type?.ToLowerInvariant() switch
            {
                "movie" => MediaType.Movie,
                "show" => MediaType.TvShow,
                _ => MediaType.Movie
            };
            list.Add(new PlexLibrary
            {
                Id = d.Id,
                Key = d.Key ?? d.Id.ToString(),
                Title = d.Title ?? "Library",
                Type = type,
                ItemCount = d.Size
            });
        }
        return list;
    }

    public Task<List<MediaCardDto>> GetLibraryContentAsync(MediaType mediaType, int page = 1, int pageSize = 20)
        => _metadata.GetLibraryAsync(mediaType, page, pageSize); // Will be replaced with Plex library calls

    public Task<MediaDetailDto?> GetMediaDetailsAsync(int mediaId, MediaType mediaType)
        => _metadata.GetDetailsAsync(mediaId, mediaType);

    public Task<List<int>> GetAvailableSeasonsAsync(int tvShowId) => Task.FromResult(new List<int>());

    public Task<bool> IsAvailableOnPlexAsync(int mediaId, MediaType mediaType) => Task.FromResult(false);

    public Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10)
        => _metadata.GetRecentlyAddedAsync(count);

    public Task<List<MediaCardDto>> SearchMediaAsync(string query, MediaType? mediaType = null)
        => _metadata.SearchAsync(query, mediaType);

    public async Task AnnotateAvailabilityAsync(List<MediaCardDto> items)
    {
        if (items == null || items.Count == 0) return;
        if (string.IsNullOrWhiteSpace(_cfg.PrimaryServerUrl) || string.IsNullOrWhiteSpace(_cfg.ServerToken)) return;
        var idx = await EnsureAvailabilityIndexAsync();
        foreach (var it in items)
        {
            if (it.IsAvailable) continue; // already known available
            var matched = false;
            // Try external ids first
            if (it.TmdbId is int tmdb && idx.ByExternal.TryGetValue($"tmdb:{tmdb}", out var rk)) matched = true;
            else if (!string.IsNullOrEmpty(it.ImdbId) && idx.ByExternal.TryGetValue($"imdb:{it.ImdbId}", out rk)) matched = true;
            else if (it.TvdbId is int tvdb && idx.ByExternal.TryGetValue($"tvdb:{tvdb}", out rk)) matched = true;
            // Fallback: title+year
            if (!matched && it.Year is int y)
            {
                var key = NormalizeTitleYear(it.Title, y);
                if (idx.ByTitleYear.Contains(key)) matched = true;
            }
            if (matched)
            {
                it.IsAvailable = true;
                // Optional: build a basic plex url (app link could be adjusted later)
                // it.PlexUrl = $"{TrimSlash(_cfg.PrimaryServerUrl)}/web/index.html#!/server/{rk}";
            }
        }
    }

    private void EnsureDefaultHeaders(HttpRequestHeaders headers)
    {
        if (!headers.Contains("X-Plex-Product"))
        {
            headers.Add("X-Plex-Product", _cfg.Product);
            headers.Add("X-Plex-Version", _cfg.Version);
            headers.Add("X-Plex-Client-Identifier", _cfg.ClientIdentifier);
            headers.Add("X-Plex-Device", _cfg.Device);
            headers.Add("X-Plex-Platform", _cfg.Platform);
            headers.Add("X-Plex-Accept", "application/json");
            headers.Accept.Clear();
            headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    private static string TrimSlash(string s) => s.EndsWith('/') ? s.TrimEnd('/') : s;

    private static string? NormalizeBaseUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim().TrimEnd('/');
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            s = "http://" + s; // default to http if scheme missing
        }
        return Uri.TryCreate(s, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;
    }

    private static string? GetBetween(string input, string start, string end)
    {
        var i = input.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        i += start.Length;
        var j = input.IndexOf(end, i, StringComparison.OrdinalIgnoreCase);
        if (j < 0) return null;
        return input.Substring(i, j - i);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Availability index and helpers
    private record AvailabilityIndex(HashSet<string> ByTitleYear, Dictionary<string, string> ByExternal, DateTime BuiltAt);
    private const string AvailabilityCacheKey = "plex_availability_index";
    private async Task<AvailabilityIndex> EnsureAvailabilityIndexAsync()
    {
        if (_cache.TryGetValue<AvailabilityIndex>(AvailabilityCacheKey, out var cached) && (DateTime.UtcNow - cached.BuiltAt) < TimeSpan.FromMinutes(10))
            return cached!;

        // Build new index
        var byTitleYear = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byExternal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var libraries = await GetLibrariesAsync();
        foreach (var lib in libraries)
        {
            // Only index Movies/Shows for now
            if (lib.Type != Shared.Enums.MediaType.Movie && lib.Type != Shared.Enums.MediaType.TvShow) continue;
            await foreach (var item in EnumerateLibraryItemsAsync(lib.Key))
            {
                var title = item.title;
                var year = item.year;
                var ratingKey = item.ratingKey;
                if (!string.IsNullOrEmpty(title) && year.HasValue)
                {
                    byTitleYear.Add(NormalizeTitleYear(title!, year.Value));
                }
                foreach (var guid in item.guids)
                {
                    // guid formats like tmdb://12345?lang=en
                    var (type, id) = ParseGuid(guid);
                    if (!string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(id))
                    {
                        var key = $"{type}:{id}";
                        if (!byExternal.ContainsKey(key)) byExternal[key] = ratingKey;
                    }
                }
            }
        }

        var index = new AvailabilityIndex(byTitleYear, byExternal, DateTime.UtcNow);
        _cache.Set(AvailabilityCacheKey, index, TimeSpan.FromMinutes(10));
        return index;
    }

    private static string NormalizeTitleYear(string? title, int year)
    {
        if (string.IsNullOrWhiteSpace(title)) return year.ToString();
        var t = new string(title.Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch)).ToArray()).Trim().ToLowerInvariant();
        t = string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return $"{t}|{year}";
    }

    private static (string? type, string? id) ParseGuid(string guid)
    {
        // e.g., tmdb://12345, imdb://tt12345, tvdb://6789
        try
        {
            var uri = new Uri(guid);
            var type = uri.Scheme; // tmdb, imdb, tvdb
            var id = uri.Host + uri.AbsolutePath; // may be like //id
            id = id.Trim('/');
            return (type, id);
        }
        catch { return (null, null); }
    }

    private async IAsyncEnumerable<(string ratingKey, string? title, int? year, List<string> guids)> EnumerateLibraryItemsAsync(string sectionKey)
    {
        int start = 0;
        const int size = 200;
        while (true)
        {
            var baseUrl = NormalizeBaseUrl(_cfg.PrimaryServerUrl);
            if (baseUrl is null) yield break;
            var url = $"{baseUrl}/library/sections/{sectionKey}/all?X-Plex-Container-Start={start}&X-Plex-Container-Size={size}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            EnsureDefaultHeaders(req.Headers);
            req.Headers.Add("X-Plex-Token", _cfg.ServerToken);
            req.Headers.Accept.Clear();
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) yield break;
            var contentType = res.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var text = await res.Content.ReadAsStringAsync();
            int returned = 0;
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                var container = JsonSerializer.Deserialize<PlexDirectoryContainer>(text, JsonOpts);
                var md = container?.MediaContainer?.Metadata;
                if (md is not null)
                {
                    foreach (var m in md)
                    {
                        returned++;
                        yield return (m.RatingKey?.ToString() ?? string.Empty, m.Title, m.Year, m.Guid ?? new List<string>());
                    }
                }
            }
            else
            {
                var x = XDocument.Parse(text);
                var metadata = x.Root?.Element("MediaContainer") ?? x.Root;
                var items = metadata?.Elements("Video");
                if (items is not null)
                {
                    foreach (var v in items)
                    {
                        returned++;
                        var rk = (string?)v.Attribute("ratingKey") ?? string.Empty;
                        var title = (string?)v.Attribute("title");
                        var year = (int?)v.Attribute("year");
                        var guids = v.Elements("Guid").Select(g => (string?)g.Attribute("id") ?? string.Empty).Where(s => !string.IsNullOrEmpty(s)).ToList();
                        yield return (rk, title, year, guids);
                    }
                }
            }
            if (returned < size) yield break;
            start += size;
            await Task.Yield();
        }
    }

    private sealed class PlexDirectoryContainer
    {
        public MediaContainer? MediaContainer { get; set; }
    }
    private sealed class MediaContainer
    {
        public List<Directory>? Directories { get; set; }
        public List<Metadata>? Metadata { get; set; }
    }
    private sealed class Directory
    {
        public int Id { get; set; }
        public string? Key { get; set; }
        public string? Title { get; set; }
        public string? Type { get; set; }
        public int Size { get; set; }
    }
    private sealed class Metadata
    {
        public int? RatingKey { get; set; }
        public string? Title { get; set; }
        public int? Year { get; set; }
        public List<string>? Guid { get; set; }
    }
}
