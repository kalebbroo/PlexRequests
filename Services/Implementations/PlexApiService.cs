using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;

namespace PlexRequestsHosted.Services.Implementations;

public class PlexApiService : IPlexApiService
{
    private readonly IMediaMetadataProvider _metadata;
    private readonly HttpClient _http;
    private readonly PlexConfiguration _cfg;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PlexApiService> _logger;
    private readonly AppDbContext _db;
    private string? _serverMachineId;

    public PlexApiService(IMediaMetadataProvider metadata, HttpClient httpClient, IOptions<PlexConfiguration> options, IMemoryCache cache, ILogger<PlexApiService> logger, AppDbContext db)
    {
        _metadata = metadata;
        _http = httpClient;
        _cfg = options.Value;
        _cache = cache;
        _logger = logger;
        _db = db;
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

        var contentType = res.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var text = await res.Content.ReadAsStringAsync();

        var list = new List<PlexLibrary>();
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            var sections = JsonSerializer.Deserialize<PlexDirectoryContainer>(text, JsonOpts);
            var dirs = sections?.MediaContainer?.Directories ?? sections?.MediaContainer?.DirectoryItems;
            if (dirs is not null)
            {
                foreach (var d in dirs)
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
            }
        }
        else
        {
            // XML fallback
            var x = XDocument.Parse(text);
            var root = x.Root?.Element("MediaContainer") ?? x.Root;
            var directories = root?.Elements("Directory");
            if (directories is not null)
            {
                foreach (var d in directories)
                {
                    var id = (int?)d.Attribute("key") ?? (int?)d.Attribute("id") ?? 0;
                    var key = (string?)d.Attribute("key") ?? id.ToString();
                    var title = (string?)d.Attribute("title") ?? "Library";
                    var typeStr = ((string?)d.Attribute("type") ?? string.Empty).ToLowerInvariant();
                    var type = typeStr == "movie" ? MediaType.Movie : typeStr == "show" ? MediaType.TvShow : MediaType.Movie;
                    var size = (int?)d.Attribute("size") ?? 0;
                    list.Add(new PlexLibrary
                    {
                        Id = id,
                        Key = key ?? id.ToString(),
                        Title = title,
                        Type = type,
                        ItemCount = size
                    });
                }
            }
        }

        if (list.Count == 0)
        {
            // Retry with token in query (some servers enforce query token vs header)
            var urlWithToken = baseUrl + $"/library/sections?X-Plex-Token={Uri.EscapeDataString(_cfg.ServerToken)}";
            using var req2 = new HttpRequestMessage(HttpMethod.Get, urlWithToken);
            EnsureDefaultHeaders(req2.Headers);
            req2.Headers.Accept.Clear();
            req2.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
            var res2 = await _http.SendAsync(req2);
            if (res2.IsSuccessStatusCode)
            {
                var txt2 = await res2.Content.ReadAsStringAsync();
                try
                {
                    var x2 = XDocument.Parse(txt2);
                    var root2 = x2.Root?.Element("MediaContainer") ?? x2.Root;
                    var directories2 = root2?.Elements("Directory");
                    if (directories2 is not null)
                    {
                        foreach (var d in directories2)
                        {
                            var key = (string?)d.Attribute("key") ?? string.Empty;
                            var idParsed = int.TryParse(key, out var idVal) ? idVal : 0;
                            var title = (string?)d.Attribute("title") ?? "Library";
                            var typeStr = ((string?)d.Attribute("type") ?? string.Empty).ToLowerInvariant();
                            var type = typeStr == "movie" ? MediaType.Movie : typeStr == "show" ? MediaType.TvShow : MediaType.Movie;
                            var size = (int?)d.Attribute("size") ?? 0;
                            list.Add(new PlexLibrary { Id = idParsed, Key = key, Title = title, Type = type, ItemCount = size });
                        }
                    }
                }
                catch { /* ignore parse errors */ }
            }
        }

        _logger.LogInformation("Fetched Plex libraries: {Count}", list.Count);
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
            string? matchPath = null;
            string rk = string.Empty;
            if (it.TmdbId is int tmdb && idx.ByExternal.TryGetValue($"tmdb:{tmdb}", out var rkTmdb)) { matched = true; rk = rkTmdb; matchPath = $"guid:tmdb:{tmdb}"; }
            else if (!string.IsNullOrEmpty(it.ImdbId) && idx.ByExternal.TryGetValue($"imdb:{it.ImdbId}", out var rkImdb)) { matched = true; rk = rkImdb; matchPath = $"guid:imdb:{it.ImdbId}"; }
            else if (it.TvdbId is int tvdb && idx.ByExternal.TryGetValue($"tvdb:{tvdb}", out var rkTvdb)) { matched = true; rk = rkTvdb; matchPath = $"guid:tvdb:{tvdb}"; }
            else if (!matched && it.TmdbId is null && it.MediaType == MediaType.Movie && it.Id > 0 && idx.ByExternal.TryGetValue($"tmdb:{it.Id}", out var rkFallback)) { matched = true; rk = rkFallback; matchPath = $"guid:tmdb:{it.Id}(fallback-from-Id)"; }
            // Fallback: title+year +/- 1 year
            if (!matched && it.Year is int y)
            {
                foreach (var yr in new int?[] { y - 1, y, y + 1 })
                {
                    var key = NormalizeTitleYear(it.Title, yr!.Value);
                    if (idx.ByTitleYear.Contains(key)) { matched = true; matchPath = $"title-year:{yr}"; idx.ByTitleYearKey.TryGetValue(key, out rk); break; }
                }
            }
            if (matched)
            {
                it.IsAvailable = true;
                if (!string.IsNullOrEmpty(rk))
                {
                    it.PlexUrl = await BuildPlexWebUrlAsync(rk);
                }
                _logger.LogInformation("Plex match success: {@Title} ({@Year}) via {Path}", it.Title, it.Year, matchPath ?? "unknown");
            }
            else
            {
                _logger.LogInformation("Plex match MISS: {@Title} ({@Year}) tmdb={TmdbId} imdb={ImdbId} tvdb={TvdbId}", it.Title, it.Year, it.TmdbId, it.ImdbId, it.TvdbId);
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
    private record AvailabilityIndex(HashSet<string> ByTitleYear, Dictionary<string, string> ByTitleYearKey, Dictionary<string, string> ByExternal, DateTime BuiltAt);
    private const string AvailabilityCacheKey = "plex_availability_index";
    private async Task<AvailabilityIndex> EnsureAvailabilityIndexAsync()
    {
        if (_cache.TryGetValue<AvailabilityIndex>(AvailabilityCacheKey, out var cached) && (DateTime.UtcNow - cached.BuiltAt) < TimeSpan.FromMinutes(10))
            return cached!;

        // Build new index
        var byTitleYear = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byTitleYearKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byExternal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var libraries = await GetLibrariesAsync();
        _logger.LogInformation("Plex index build: {LibCount} libraries", libraries.Count);
        foreach (var lib in libraries)
        {
            // Only index Movies/Shows for now
            if (lib.Type != Shared.Enums.MediaType.Movie && lib.Type != Shared.Enums.MediaType.TvShow) continue;
            int added = 0;
            await foreach (var item in EnumerateLibraryItemsAsync(lib.Key))
            {
                var title = item.title;
                var year = item.year;
                var ratingKey = item.ratingKey;
                if (!string.IsNullOrEmpty(title) && year.HasValue)
                {
                    var ty = NormalizeTitleYear(title, year.Value);
                    byTitleYear.Add(ty);
                    if (!string.IsNullOrEmpty(ratingKey) && !byTitleYearKey.ContainsKey(ty))
                        byTitleYearKey[ty] = ratingKey;
                    added++;
                }
                foreach (var guid in item.guids)
                {
                    // guid formats like tmdb://12345?lang=en
                    var (type, id) = ParseGuid(guid);
                    if (!string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(id))
                    {
                        var key = $"{type}:{id}";
                        if (!byExternal.ContainsKey(key)) byExternal[key] = ratingKey;
                        // Persist mapping to DB (upsert by ExternalKey)
                        try
                        {
                            var existing = _db.PlexMappings.FirstOrDefault(m => m.ExternalKey == key);
                            if (existing is null)
                            {
                                _db.PlexMappings.Add(new PlexMappingEntity
                                {
                                    ExternalKey = key,
                                    RatingKey = ratingKey,
                                    MediaType = lib.Type,
                                    Title = title,
                                    Year = year,
                                    LastSeenAt = DateTime.UtcNow
                                });
                            }
                            else
                            {
                                existing.RatingKey = ratingKey;
                                existing.MediaType = lib.Type;
                                existing.Title = title;
                                existing.Year = year;
                                existing.LastSeenAt = DateTime.UtcNow;
                            }
                        }
                        catch { /* best-effort; avoid blocking index */ }
                    }
                }
            }
            _logger.LogInformation("Indexed library {LibTitle} items: {Count}", lib.Title, added);
            try { await _db.SaveChangesAsync(); } catch { /* best-effort */ }
        }

        var index = new AvailabilityIndex(byTitleYear, byTitleYearKey, byExternal, DateTime.UtcNow);
        _logger.LogInformation("Plex index built. TitleYear={TitleCount} External={ExternalCount}", byTitleYear.Count, byExternal.Count);
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

    private async Task<string?> EnsureServerMachineIdAsync()
    {
        if (!string.IsNullOrEmpty(_serverMachineId)) return _serverMachineId;
        var baseUrl = NormalizeBaseUrl(_cfg.PrimaryServerUrl);
        if (baseUrl is null) return null;
        var url = baseUrl + "/?X-Plex-Token=" + Uri.EscapeDataString(_cfg.ServerToken ?? string.Empty);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureDefaultHeaders(req.Headers);
        var res = await _http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return null;
        var txt = await res.Content.ReadAsStringAsync();
        try
        {
            var x = XDocument.Parse(txt);
            var root = x.Root?.Element("MediaContainer") ?? x.Root;
            var mid = (string?)root?.Attribute("machineIdentifier");
            if (!string.IsNullOrWhiteSpace(mid)) _serverMachineId = mid;
        }
        catch { /* ignore */ }
        return _serverMachineId;
    }

    private async Task<string> BuildPlexWebUrlAsync(string ratingKey)
    {
        // Prefer app.plex.tv with server machine id
        var machineId = await EnsureServerMachineIdAsync();
        var encodedKey = Uri.EscapeDataString($"/library/metadata/{ratingKey}");
        if (!string.IsNullOrEmpty(machineId))
        {
            return $"https://app.plex.tv/desktop#!/server/{machineId}/details?key={encodedKey}";
        }
        // Fallback to server's web app without machine id (may still resolve)
        var baseUrl = NormalizeBaseUrl(_cfg.PrimaryServerUrl) ?? string.Empty;
        return $"{baseUrl}/web/index.html#!/details?key={encodedKey}";
    }

    private async IAsyncEnumerable<(string ratingKey, string? title, int? year, List<string> guids)> EnumerateLibraryItemsAsync(string sectionKey)
    {
        int start = 0;
        const int size = 200;
        while (true)
        {
            var baseUrl = NormalizeBaseUrl(_cfg.PrimaryServerUrl);
            if (baseUrl is null) yield break;
            var token = _cfg.ServerToken ?? string.Empty;
            var url = $"{baseUrl}/library/sections/{sectionKey}/all?X-Plex-Container-Start={start}&X-Plex-Container-Size={size}&includeGuids=1&X-Plex-Token={Uri.EscapeDataString(token)}";
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
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var root = doc.RootElement;
                    var mc = root.TryGetProperty("MediaContainer", out var mcEl) ? mcEl : root;
                    if (mc.ValueKind == JsonValueKind.Object && mc.TryGetProperty("Metadata", out var mdEl) && mdEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var m in mdEl.EnumerateArray())
                        {
                            returned++;
                            string ratingKey = string.Empty;
                            if (m.TryGetProperty("ratingKey", out var rkEl))
                            {
                                ratingKey = rkEl.ValueKind == JsonValueKind.String ? rkEl.GetString() ?? string.Empty : rkEl.ValueKind == JsonValueKind.Number ? rkEl.GetInt32().ToString() : string.Empty;
                            }
                            string? title = m.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() : null;
                            int? year = null;
                            if (m.TryGetProperty("year", out var yEl))
                            {
                                if (yEl.ValueKind == JsonValueKind.Number) year = yEl.GetInt32();
                                else if (yEl.ValueKind == JsonValueKind.String && int.TryParse(yEl.GetString(), out var yv)) year = yv;
                            }
                            var guids = new List<string>();
                            if (m.TryGetProperty("Guid", out var guidArr) && guidArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var g in guidArr.EnumerateArray())
                                {
                                    if (g.ValueKind == JsonValueKind.Object && g.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                                    {
                                        var s = idEl.GetString();
                                        if (!string.IsNullOrWhiteSpace(s)) guids.Add(s!);
                                    }
                                }
                            }
                            else if (m.TryGetProperty("guid", out var guidStr) && guidStr.ValueKind == JsonValueKind.String)
                            {
                                var s = guidStr.GetString();
                                if (!string.IsNullOrWhiteSpace(s)) guids.Add(s!); // may be plex:// guid; still capture
                            }
                            yield return (ratingKey, title, year, guids);
                        }
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
        // JSON uses 'Directory' instead of 'Directories'
        public List<Directory>? DirectoryItems { get; set; }
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
        // ratingKey can be a string in JSON; treat as string universally
        public string? RatingKey { get; set; }
        public string? Title { get; set; }
        public int? Year { get; set; }
        // In JSON, GUIDs are objects: { "id": "tmdb://..." }
        public List<PlexGuid>? Guid { get; set; }
    }

    private sealed class PlexGuid
    {
        public string? Id { get; set; }
    }

    // Diagnostics
    public async Task<object> GetIndexStatsAsync()
    {
        var idx = await EnsureAvailabilityIndexAsync();
        return new { titleYearCount = idx.ByTitleYear.Count, externalCount = idx.ByExternal.Count, builtAt = idx.BuiltAt };
    }

    public async Task<object> TestMatchAsync(string? title, int? year, int? tmdbId, string? imdbId, int? tvdbId, MediaType mediaType)
    {
        var idx = await EnsureAvailabilityIndexAsync();
        var reasons = new List<string>();
        if (tmdbId.HasValue && idx.ByExternal.ContainsKey($"tmdb:{tmdbId.Value}")) return new { matched = true, path = $"guid:tmdb:{tmdbId.Value}" };
        if (!string.IsNullOrWhiteSpace(imdbId) && idx.ByExternal.ContainsKey($"imdb:{imdbId}")) return new { matched = true, path = $"guid:imdb:{imdbId}" };
        if (tvdbId.HasValue && idx.ByExternal.ContainsKey($"tvdb:{tvdbId.Value}")) return new { matched = true, path = $"guid:tvdb:{tvdbId.Value}" };
        if (year.HasValue && !string.IsNullOrWhiteSpace(title))
        {
            foreach (var yr in new[] { year.Value - 1, year.Value, year.Value + 1 })
            {
                if (idx.ByTitleYear.Contains(NormalizeTitleYear(title, yr))) return new { matched = true, path = $"title-year:{yr}" };
            }
            reasons.Add("title-year miss");
        }
        else reasons.Add("missing title/year");
        return new { matched = false, reasons };
    }

    // Force rebuild
    public async Task<object> RebuildAvailabilityIndexAsync()
    {
        _cache.Remove(AvailabilityCacheKey);
        var idx = await EnsureAvailabilityIndexAsync();
        return new { rebuilt = true, titleYearCount = idx.ByTitleYear.Count, externalCount = idx.ByExternal.Count, builtAt = idx.BuiltAt };
    }

    // Low-level helpers
    public async Task<string> GetSectionsRawAsync()
    {
        var baseUrl = NormalizeBaseUrl(_cfg.PrimaryServerUrl) ?? throw new InvalidOperationException("No Plex base URL");
        var token = _cfg.ServerToken ?? string.Empty;
        var url = $"{baseUrl}/library/sections?X-Plex-Token={Uri.EscapeDataString(token)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureDefaultHeaders(req.Headers);
        var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync();
    }

    public async Task<object> GetMetadataAsync(string ratingKey)
    {
        var baseUrl = NormalizeBaseUrl(_cfg.PrimaryServerUrl) ?? throw new InvalidOperationException("No Plex base URL");
        var token = _cfg.ServerToken ?? string.Empty;
        var url = $"{baseUrl}/library/metadata/{Uri.EscapeDataString(ratingKey)}?X-Plex-Token={Uri.EscapeDataString(token)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureDefaultHeaders(req.Headers);
        var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var text = await res.Content.ReadAsStringAsync();
        try
        {
            // Try JSON first
            var container = JsonSerializer.Deserialize<PlexDirectoryContainer>(text, JsonOpts);
            if (container?.MediaContainer?.Metadata is not null)
            {
                var md = container.MediaContainer.Metadata.First();
                var guids = md.Guid?.Select(g => g.Id ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
                return new { ratingKey, md.Title, md.Year, Guids = guids };
            }
        }
        catch { /* fall back to XML */ }
        try
        {
            var x = XDocument.Parse(text);
            var m = x.Root?.Descendants("Video").FirstOrDefault();
            if (m is not null)
            {
                var title = (string?)m.Attribute("title");
                var year = (int?)m.Attribute("year");
                var guids = m.Elements("Guid").Select(g => (string?)g.Attribute("id") ?? string.Empty).Where(s => !string.IsNullOrEmpty(s)).ToList();
                return new { ratingKey, Title = title, Year = year, Guids = guids };
            }
        }
        catch { }
        return new { ratingKey, raw = text };
    }

    public async Task<List<object>> SearchServerAsync(string query, MediaType? mediaType)
    {
        var baseUrl = NormalizeBaseUrl(_cfg.PrimaryServerUrl) ?? throw new InvalidOperationException("No Plex base URL");
        var token = _cfg.ServerToken ?? string.Empty;
        var typeParam = mediaType == MediaType.TvShow ? "2" : mediaType == MediaType.Movie ? "1" : string.Empty;
        var url = $"{baseUrl}/library/search?query={Uri.EscapeDataString(query)}&X-Plex-Token={Uri.EscapeDataString(token)}" + (string.IsNullOrEmpty(typeParam) ? string.Empty : $"&type={typeParam}");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureDefaultHeaders(req.Headers);
        var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var text = await res.Content.ReadAsStringAsync();
        var results = new List<object>();
        try
        {
            var container = JsonSerializer.Deserialize<PlexDirectoryContainer>(text, JsonOpts);
            var md = container?.MediaContainer?.Metadata;
            if (md is not null)
            {
                foreach (var m in md)
                {
                    var guids = m.Guid?.Select(g => g.Id ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
                    results.Add(new { RatingKey = m.RatingKey ?? string.Empty, m.Title, m.Year, Guids = guids });
                }
                return results;
            }
        }
        catch { }
        try
        {
            var x = XDocument.Parse(text);
            foreach (var v in x.Root?.Descendants("Video") ?? Enumerable.Empty<XElement>())
            {
                var rk = (string?)v.Attribute("ratingKey") ?? string.Empty;
                var title = (string?)v.Attribute("title");
                var year = (int?)v.Attribute("year");
                var guids = v.Elements("Guid").Select(g => (string?)g.Attribute("id") ?? string.Empty).Where(s => !string.IsNullOrEmpty(s)).ToList();
                results.Add(new { RatingKey = rk, Title = title, Year = year, Guids = guids });
            }
        }
        catch { }
        return results;
    }

    public async Task<List<object>> ResolveByTitleAsync(string title, int? year, MediaType mediaType, int maxResults = 5)
    {
        // Use server search then filter by normalized title and ±1 year tolerance
        var results = await SearchServerAsync(title, mediaType);
        string Norm(string s)
        {
            var t = new string(s.Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch)).ToArray()).Trim().ToLowerInvariant();
            return string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        var normTitle = Norm(title);
        var filtered = results
            .Select(r => new
            {
                r,
                Title = (string?)r.GetType().GetProperty("Title")?.GetValue(r) ?? string.Empty,
                Year = (int?)r.GetType().GetProperty("Year")?.GetValue(r),
                RatingKey = (string?)r.GetType().GetProperty("RatingKey")?.GetValue(r) ?? string.Empty
            })
            .Where(x => !string.IsNullOrEmpty(x.RatingKey))
            .Where(x => Norm(x.Title) == normTitle)
            .Where(x => !year.HasValue || (x.Year.HasValue && Math.Abs(x.Year.Value - year.Value) <= 1))
            .Take(maxResults)
            .Select(x => (object)new { x.RatingKey, x.Title, x.Year })
            .ToList();
        return filtered;
    }
}
