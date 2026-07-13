using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
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

        try
        {
            var baseUrl = NormalizeBaseUrl(_cfg.PrimaryServerUrl);
            if (baseUrl is null) return null;
            // Use the lightweight /identity endpoint (a couple hundred bytes) rather than the root "/"
            // (which returns the full ~10KB server payload). This is a health check that only needs
            // online-status + version; the small response also avoids stalling on links (e.g. a VPN
            // tunnel with a low MTU) where large responses can hang. friendlyName isn't in /identity,
            // so Name falls back to the default below — cosmetic only.
            var url = baseUrl + "/identity";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            EnsureDefaultHeaders(req.Headers);
            req.Headers.Add("X-Plex-Token", _cfg.ServerToken);
            var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return new PlexServerInfo { IsOnline = false };

            var text = await res.Content.ReadAsStringAsync();
            // /identity returns machineIdentifier + version; friendlyName is absent, so Name falls back.
            var name = GetBetween(text, "friendlyName=\"", "\"") ?? "Plex Server";
            var version = GetBetween(text, "version=\"", "\"") ?? string.Empty;
            return new PlexServerInfo { Name = name, Version = version, IsOnline = true };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Plex server unreachable at {Url}", _cfg.PrimaryServerUrl);
            return new PlexServerInfo { IsOnline = false };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Plex server info");
            return new PlexServerInfo { IsOnline = false };
        }
    }

    public async Task<List<PlexLibrary>> GetLibrariesAsync()
    {
        if (string.IsNullOrWhiteSpace(_cfg.PrimaryServerUrl) || string.IsNullOrWhiteSpace(_cfg.ServerToken))
            return new();

        try
        {
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
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Plex server unreachable while fetching libraries from {Url}", _cfg.PrimaryServerUrl);
            return new List<PlexLibrary>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Plex libraries");
            return new List<PlexLibrary>();
        }
    }

    public Task<List<MediaCardDto>> GetLibraryContentAsync(MediaType mediaType, int page = 1, int pageSize = 20)
        => _metadata.GetLibraryAsync(mediaType, page, pageSize); // Will be replaced with Plex library calls

    public Task<MediaDetailDto?> GetMediaDetailsAsync(int mediaId, MediaType mediaType)
        => _metadata.GetDetailsAsync(mediaId, mediaType);

    // Episodes of a season with per-episode "already on Plex" overlaid from the DB index.
    public async Task<List<EpisodeDto>> GetSeasonEpisodesAsync(int showId, int seasonNumber)
    {
        var episodes = await _metadata.GetSeasonEpisodesAsync(showId, seasonNumber);
        if (episodes.Count == 0) return episodes;
        var ratingKey = await _db.PlexMappings.Where(m => m.ExternalKey == $"tmdb:{showId}").Select(m => m.RatingKey).FirstOrDefaultAsync();
        if (!string.IsNullOrEmpty(ratingKey))
        {
            var csv = await _db.PlexSeasonAvailability
                .Where(s => s.ShowRatingKey == ratingKey && s.SeasonNumber == seasonNumber)
                .Select(s => s.AvailableEpisodesCsv).FirstOrDefaultAsync();
            if (!string.IsNullOrEmpty(csv))
            {
                var have = csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => int.TryParse(x, out var n) ? n : -1).ToHashSet();
                foreach (var ep in episodes) ep.IsAvailable = have.Contains(ep.EpisodeNumber);
            }
        }
        return episodes;
    }

    // Which seasons of a TMDB show are already on Plex (from the DB availability index).
    public async Task<List<int>> GetAvailableSeasonsAsync(int tvShowId)
    {
        var key = $"tmdb:{tvShowId}";
        var ratingKey = await _db.PlexMappings.Where(m => m.ExternalKey == key).Select(m => m.RatingKey).FirstOrDefaultAsync();
        if (string.IsNullOrEmpty(ratingKey)) return new List<int>();
        return await _db.PlexSeasonAvailability
            .Where(s => s.ShowRatingKey == ratingKey && s.EpisodeCount > 0)
            .OrderBy(s => s.SeasonNumber)
            .Select(s => s.SeasonNumber)
            .ToListAsync();
    }

    public Task<bool> IsAvailableOnPlexAsync(int mediaId, MediaType mediaType) => Task.FromResult(false);

    public Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10)
        => _metadata.GetRecentlyAddedAsync(count);

    public Task<List<MediaCardDto>> GetTrendingAsync(MediaType? mediaType = null, int page = 1, int pageSize = 20)
        => _metadata.GetTrendingAsync(mediaType, page, pageSize);

    public Task<List<MediaCardDto>> GetPopularAsync(MediaType mediaType, int page = 1, int pageSize = 20)
        => _metadata.GetPopularAsync(mediaType, page, pageSize);

    public Task<List<MediaCardDto>> GetTopRatedAsync(MediaType mediaType, int page = 1, int pageSize = 20)
        => _metadata.GetTopRatedAsync(mediaType, page, pageSize);

    public Task<List<MediaCardDto>> GetByGenreAsync(MediaType mediaType, string genre, int page = 1, int pageSize = 20)
        => _metadata.GetByGenreAsync(mediaType, genre, page, pageSize);

    public Task<List<MediaCardDto>> GetSimilarAsync(int mediaId, MediaType mediaType, int count = 12)
        => _metadata.GetSimilarAsync(mediaId, mediaType, count);

    public Task<List<MediaCardDto>> SearchMediaAsync(string query, MediaType? mediaType = null)
        => _metadata.SearchAsync(query, mediaType);

    public async Task AnnotateAvailabilityAsync(List<MediaCardDto> items)
    {
        if (items == null || items.Count == 0) return;
        if (string.IsNullOrWhiteSpace(_cfg.PrimaryServerUrl) || string.IsNullOrWhiteSpace(_cfg.ServerToken)) return;

        try
        {
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
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Plex server unreachable while annotating availability. Continuing without Plex availability data.");
            // Continue without Plex data - items will simply not be marked as available
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error annotating Plex availability. Continuing without Plex availability data.");
            // Continue without Plex data - items will simply not be marked as available
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
    // Fast read path: the in-memory match index is projected FROM THE DB (kept fresh by the
    // background AvailabilityRefreshService). This never hits Plex, so annotating a page is cheap.
    private async Task<AvailabilityIndex> EnsureAvailabilityIndexAsync()
    {
        if (_cache.TryGetValue<AvailabilityIndex>(AvailabilityCacheKey, out var cached) && (DateTime.UtcNow - cached.BuiltAt) < TimeSpan.FromMinutes(5))
            return cached!;

        var byTitleYear = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byTitleYearKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byExternal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var rows = await _db.PlexMappings.AsNoTracking()
            .Select(m => new { m.ExternalKey, m.RatingKey, m.Title, m.Year })
            .ToListAsync();
        foreach (var m in rows)
        {
            if (!string.IsNullOrEmpty(m.ExternalKey) && !byExternal.ContainsKey(m.ExternalKey))
                byExternal[m.ExternalKey] = m.RatingKey;
            if (!string.IsNullOrEmpty(m.Title) && m.Year.HasValue)
            {
                var ty = NormalizeTitleYear(m.Title, m.Year.Value);
                byTitleYear.Add(ty);
                if (!byTitleYearKey.ContainsKey(ty)) byTitleYearKey[ty] = m.RatingKey;
            }
        }

        var index = new AvailabilityIndex(byTitleYear, byTitleYearKey, byExternal, DateTime.UtcNow);
        _cache.Set(AvailabilityCacheKey, index, TimeSpan.FromMinutes(5));
        return index;
    }

    // Expensive write path: scan Plex and upsert the availability tables (item id-maps + per-season
    // episode presence), then prune anything not seen this pass (removed from the server). Called by
    // the background refresh service and the manual rebuild endpoint.
    public async Task<object> RebuildAvailabilityFromPlexAsync()
    {
        if (string.IsNullOrWhiteSpace(_cfg.PrimaryServerUrl) || string.IsNullOrWhiteSpace(_cfg.ServerToken))
            return new { skipped = true, reason = "Plex not configured" };

        var scanStart = DateTime.UtcNow;
        int maps = 0, episodes = 0, seasons = 0;
        var libraries = await GetLibrariesAsync();
        _logger.LogInformation("Plex availability scan: {LibCount} libraries", libraries.Count);

        foreach (var lib in libraries)
        {
            if (lib.Type != Shared.Enums.MediaType.Movie && lib.Type != Shared.Enums.MediaType.TvShow) continue;

            // Items (movies + shows) -> external id -> ratingKey mappings.
            await foreach (var item in EnumerateLibraryItemsAsync(lib.Key))
            {
                foreach (var guid in item.guids)
                {
                    var (type, id) = ParseGuid(guid);
                    if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(id)) continue;
                    var key = $"{type}:{id}";
                    var existing = await _db.PlexMappings.FirstOrDefaultAsync(m => m.ExternalKey == key);
                    if (existing is null)
                        _db.PlexMappings.Add(new PlexMappingEntity { ExternalKey = key, RatingKey = item.ratingKey, MediaType = lib.Type, Title = item.title, Year = item.year, LastSeenAt = scanStart });
                    else { existing.RatingKey = item.ratingKey; existing.MediaType = lib.Type; existing.Title = item.title; existing.Year = item.year; existing.LastSeenAt = scanStart; }
                    maps++;
                }
            }
            await _db.SaveChangesAsync();

            // Episodes -> per-season presence (TV libraries only). One type=4 query returns every
            // episode with its show (grandparentRatingKey) + season (parentIndex) + number (index).
            if (lib.Type == Shared.Enums.MediaType.TvShow)
            {
                var perSeason = new Dictionary<(string show, int season), SortedSet<int>>();
                await foreach (var ep in EnumerateEpisodesAsync(lib.Key))
                {
                    var k = (ep.showRatingKey, ep.season);
                    if (!perSeason.TryGetValue(k, out var set)) { set = new SortedSet<int>(); perSeason[k] = set; }
                    if (ep.episode > 0) set.Add(ep.episode);
                    episodes++;
                }
                foreach (var (k, set) in perSeason)
                {
                    var csv = string.Join(",", set);
                    var row = await _db.PlexSeasonAvailability.FirstOrDefaultAsync(s => s.ShowRatingKey == k.show && s.SeasonNumber == k.season);
                    if (row is null)
                        _db.PlexSeasonAvailability.Add(new PlexSeasonAvailabilityEntity { ShowRatingKey = k.show, SeasonNumber = k.season, AvailableEpisodesCsv = csv, EpisodeCount = set.Count, LastSeenAt = scanStart });
                    else { row.AvailableEpisodesCsv = csv; row.EpisodeCount = set.Count; row.LastSeenAt = scanStart; }
                    seasons++;
                }
                await _db.SaveChangesAsync();
            }
        }

        // Prune rows not touched this pass: they were removed from the server.
        var staleMaps = await _db.PlexMappings.Where(m => m.LastSeenAt < scanStart).ToListAsync();
        var staleSeasons = await _db.PlexSeasonAvailability.Where(s => s.LastSeenAt < scanStart).ToListAsync();
        if (staleMaps.Count > 0) _db.PlexMappings.RemoveRange(staleMaps);
        if (staleSeasons.Count > 0) _db.PlexSeasonAvailability.RemoveRange(staleSeasons);
        await _db.SaveChangesAsync();

        _cache.Remove(AvailabilityCacheKey); // force the read projection to rebuild from fresh DB
        _logger.LogInformation("Plex availability rebuilt: {Maps} id-maps, {Seasons} seasons, {Eps} episodes; pruned {PMaps} maps / {PSeasons} seasons",
            maps, seasons, episodes, staleMaps.Count, staleSeasons.Count);
        return new { maps, seasons, episodes, prunedMaps = staleMaps.Count, prunedSeasons = staleSeasons.Count, at = scanStart };
    }

    // Enumerate every episode in a TV library section via a single paged type=4 query.
    private async IAsyncEnumerable<(string showRatingKey, int season, int episode)> EnumerateEpisodesAsync(string sectionKey)
    {
        var baseUrl = NormalizeBaseUrl(_cfg.PrimaryServerUrl);
        if (baseUrl is null) yield break;
        var token = _cfg.ServerToken ?? string.Empty;
        int start = 0; const int size = 200;
        while (true)
        {
            var url = $"{baseUrl}/library/sections/{sectionKey}/all?type=4&X-Plex-Container-Start={start}&X-Plex-Container-Size={size}&X-Plex-Token={Uri.EscapeDataString(token)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            EnsureDefaultHeaders(req.Headers);
            req.Headers.Add("X-Plex-Token", _cfg.ServerToken);
            req.Headers.Accept.Clear();
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) yield break;
            var text = await res.Content.ReadAsStringAsync();
            int returned = 0;
            using (var doc = JsonDocument.Parse(text))
            {
                var root = doc.RootElement;
                var mc = root.TryGetProperty("MediaContainer", out var mcEl) ? mcEl : root;
                if (mc.ValueKind == JsonValueKind.Object && mc.TryGetProperty("Metadata", out var mdEl) && mdEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in mdEl.EnumerateArray())
                    {
                        returned++;
                        var show = JsonStringOrNumber(m, "grandparentRatingKey");
                        var season = JsonInt(m, "parentIndex");
                        var episode = JsonInt(m, "index");
                        if (!string.IsNullOrEmpty(show) && season is >= 0)
                            yield return (show, season.Value, episode ?? -1);
                    }
                }
            }
            if (returned < size) yield break;
            start += size;
        }
    }

    private static string JsonStringOrNumber(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var p)) return string.Empty;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString() ?? string.Empty,
            JsonValueKind.Number => p.GetInt64().ToString(),
            _ => string.Empty
        };
    }

    private static int? JsonInt(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n)) return n;
        if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var s)) return s;
        return null;
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

        try
        {
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
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Plex server unreachable while fetching machine ID");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Plex server machine ID");
            return null;
        }
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

    // Force a full Plex -> DB rescan (used by the admin endpoint and after a fulfillment completes).
    public async Task<object> RebuildAvailabilityIndexAsync() => await RebuildAvailabilityFromPlexAsync();

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
