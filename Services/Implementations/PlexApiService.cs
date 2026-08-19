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
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Services.Implementations;

public class PlexApiService : IPlexApiService
{
    private readonly IMediaMetadataProvider _metadata;
    private readonly HttpClient _http;
    private readonly PlexConfiguration _cfg;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PlexApiService> _logger;
    private readonly AppDbContext _db;
    private readonly ISeasonAvailabilityEvaluator _seasonEvaluator;
    private string? _serverMachineId;
    private const string UsageInsightsCacheKey = "plex_admin_usage_insights";
    private const string UsageInsightsLastGoodCacheKey = "plex_admin_usage_insights_last_good";
    private static readonly SemaphoreSlim UsageInsightsLock = new(1, 1);

    public PlexApiService(IMediaMetadataProvider metadata, HttpClient httpClient, IOptions<PlexConfiguration> options, IMemoryCache cache, ILogger<PlexApiService> logger, AppDbContext db, ISeasonAvailabilityEvaluator seasonEvaluator)
    {
        _metadata = metadata;
        _http = httpClient;
        _cfg = options.Value;
        _cache = cache;
        _logger = logger;
        _db = db;
        _seasonEvaluator = seasonEvaluator;
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

            // /identity is the reliable online/version check: a tiny (~200 byte) response that still
            // gets through on constrained links (e.g. a VPN egress path with a low, unclamped MTU)
            // where a larger response can silently black-hole. IsOnline must never be gated on a
            // bigger payload than this.
            var (_, version, _, _, machineId) = await FetchServerRootAsync(baseUrl, "/identity", CancellationToken.None);
            if (version is null) return new PlexServerInfo { IsOnline = false };
            if (!string.IsNullOrWhiteSpace(machineId)) _serverMachineId = machineId;

            // Best-effort enrichment: root "/" also has friendlyName + platform, but it's a larger
            // payload than /identity, so give it a short leash — a slow/blocked path here must
            // degrade to the /identity-only info above, not report the whole server as offline.
            string? name = null, platform = null, platformVersion = null;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var root = await FetchServerRootAsync(baseUrl, "/", cts.Token);
                name = root.name; platform = root.platform; platformVersion = root.platformVersion;
                if (!string.IsNullOrWhiteSpace(root.machineId)) _serverMachineId = root.machineId;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Plex root '/' enrichment timed out; using /identity data only");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Plex root '/' enrichment failed; using /identity data only");
            }

            return new PlexServerInfo
            {
                Name = name ?? "Plex Server",
                Version = version ?? string.Empty,
                Platform = platform ?? string.Empty,
                PlatformVersion = platformVersion ?? string.Empty,
                IsOnline = true
            };
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

    private async Task<(string? name, string? version, string? platform, string? platformVersion, string? machineId)> FetchServerRootAsync(string baseUrl, string path, CancellationToken ct)
    {
        var url = baseUrl + path;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureDefaultHeaders(req.Headers);
        req.Headers.Add("X-Plex-Token", _cfg.ServerToken);
        var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return (null, null, null, null, null);
        var contentType = res.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var text = await res.Content.ReadAsStringAsync(ct);
        return ParseServerRoot(contentType, text);
    }

    private static (string? name, string? version, string? platform, string? platformVersion, string? machineId) ParseServerRoot(string contentType, string text)
    {
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var mc = root.TryGetProperty("MediaContainer", out var mcEl) ? mcEl : root;
            string? Get(string prop) => mc.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            return (Get("friendlyName"), Get("version"), Get("platform"), Get("platformVersion"), Get("machineIdentifier"));
        }
        else
        {
            var x = XDocument.Parse(text);
            var root = x.Root?.Name.LocalName == "MediaContainer" ? x.Root : x.Root?.Element("MediaContainer") ?? x.Root;
            string? Attr(string name) => (string?)root?.Attribute(name);
            return (Attr("friendlyName"), Attr("version"), Attr("platform"), Attr("platformVersion"), Attr("machineIdentifier"));
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
            var response = await SendPlexAsync(baseUrl + "/library/sections", 0, 200);
            if (response is null) return new();

            var sections = ParseLibrarySections(response.Value.ContentType, response.Value.Body);
            if (sections.Count == 0) return sections;

            // /library/sections intentionally contains section metadata, not aggregate item counts. Fetch
            // each section's actual content and collection containers; their totalSize values are the
            // authoritative, pagination-safe metrics. A failed child query leaves that metric null rather
            // than displaying a misleading zero.
            var enriched = await Task.WhenAll(sections.Select(EnrichLibraryAsync));
            _logger.LogInformation("Fetched {Count} Plex libraries with content totals", enriched.Length);
            return enriched.ToList();
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

    public async Task<PlexServerDashboard> GetServerDashboardAsync()
    {
        var serverTask = GetServerInfoAsync();
        var librariesTask = GetLibrariesAsync();
        var sessionsTask = GetActiveSessionsAsync();
        var usageTask = GetUsageInsightsAsync();
        await Task.WhenAll(serverTask, librariesTask, sessionsTask, usageTask);

        var libraries = librariesTask.Result;
        var server = serverTask.Result;
        if (server is not null) server = server with { LibraryCount = libraries.Count };
        return new PlexServerDashboard
        {
            Server = server,
            Libraries = libraries,
            Sessions = sessionsTask.Result,
            Usage = usageTask.Result
        };
    }

    private async Task<PlexLibrary> EnrichLibraryAsync(PlexLibrary library)
    {
        var contentTask = GetContainerPageSafeAsync($"/library/sections/{Uri.EscapeDataString(library.Key)}/all?sort=addedAt%3Adesc", 4);
        var collectionsTask = GetContainerPageSafeAsync($"/library/sections/{Uri.EscapeDataString(library.Key)}/collections", 0);
        Task<PlexContainerPage?>? childrenTask = library.Type switch
        {
            MediaType.TvShow => GetContainerPageSafeAsync($"/library/sections/{Uri.EscapeDataString(library.Key)}/all?type=4", 0),
            MediaType.Music => GetContainerPageSafeAsync($"/library/sections/{Uri.EscapeDataString(library.Key)}/all?type=10", 0),
            _ => null
        };

        if (childrenTask is null)
            await Task.WhenAll(contentTask, collectionsTask);
        else
            await Task.WhenAll(contentTask, collectionsTask, childrenTask);

        var content = contentTask.Result;
        var collections = collectionsTask.Result;
        var children = childrenTask?.Result;
        return library with
        {
            ItemCount = content?.TotalSize,
            ChildItemCount = children?.TotalSize,
            ChildItemLabel = library.Type == MediaType.TvShow ? "episodes" : library.Type == MediaType.Music ? "tracks" : null,
            CollectionCount = collections?.TotalSize,
            RecentlyAdded = content?.Items ?? new()
        };
    }

    private async Task<PlexContainerPage?> GetContainerPageSafeAsync(string relativePath, int pageSize)
    {
        try
        {
            var baseUrl = NormalizeBaseUrl(_cfg.PrimaryServerUrl);
            if (baseUrl is null) return null;
            var response = await SendPlexAsync(baseUrl + relativePath, 0, pageSize);
            return response is null ? null : ParseContainerPage(response.Value.ContentType, response.Value.Body, response.Value.TotalSizeHeader);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plex metric query failed for {Path}", relativePath.Split('?')[0]);
            return null;
        }
    }

    private async Task<(string ContentType, string Body, int? TotalSizeHeader)?> SendPlexAsync(
        string url,
        int containerStart,
        int containerSize,
        CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureDefaultHeaders(req.Headers);
        req.Headers.Add("X-Plex-Token", _cfg.ServerToken);
        req.Headers.Add("X-Plex-Container-Start", containerStart.ToString());
        req.Headers.Add("X-Plex-Container-Size", containerSize.ToString());
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var res = await _http.SendAsync(req, cancellationToken);
        if (!res.IsSuccessStatusCode) return null;
        int? totalHeader = null;
        if (res.Headers.TryGetValues("X-Plex-Container-Total-Size", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var parsed)) totalHeader = parsed;
        return (res.Content.Headers.ContentType?.MediaType ?? string.Empty,
            await res.Content.ReadAsStringAsync(cancellationToken), totalHeader);
    }

    internal static List<PlexLibrary> ParseLibrarySections(string contentType, string body)
    {
        var result = new List<PlexLibrary>();
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || body.TrimStart().StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var mc = root.TryGetProperty("MediaContainer", out var mediaContainer) ? mediaContainer : root;
            if (!mc.TryGetProperty("Directory", out var directories) || directories.ValueKind != JsonValueKind.Array) return result;
            foreach (var directory in directories.EnumerateArray())
            {
                var key = JsonStringOrNumber(directory, "key");
                if (string.IsNullOrWhiteSpace(key)) continue;
                var plexType = JsonStr(directory, "type")?.ToLowerInvariant();
                result.Add(new PlexLibrary
                {
                    Id = int.TryParse(key, out var id) ? id : 0,
                    Key = key,
                    Title = JsonStr(directory, "title") ?? "Library",
                    Type = PlexMediaType(plexType),
                    ItemLabel = plexType switch { "movie" => "movies", "show" => "shows", "artist" => "artists", _ => "titles" },
                    IsRefreshing = JsonBool(directory, "refreshing"),
                    LastScannedAt = UnixDate(JsonLong(directory, "scannedAt"))
                });
            }
            return result;
        }

        var xml = XDocument.Parse(body);
        var xmlRoot = xml.Root?.Name.LocalName == "MediaContainer" ? xml.Root : xml.Root?.Element("MediaContainer") ?? xml.Root;
        foreach (var directory in xmlRoot?.Elements("Directory") ?? Enumerable.Empty<XElement>())
        {
            var key = (string?)directory.Attribute("key") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key)) continue;
            var plexType = ((string?)directory.Attribute("type") ?? string.Empty).ToLowerInvariant();
            result.Add(new PlexLibrary
            {
                Id = int.TryParse(key, out var id) ? id : 0,
                Key = key,
                Title = (string?)directory.Attribute("title") ?? "Library",
                Type = PlexMediaType(plexType),
                ItemLabel = plexType switch { "movie" => "movies", "show" => "shows", "artist" => "artists", _ => "titles" },
                IsRefreshing = (bool?)directory.Attribute("refreshing") ?? false,
                LastScannedAt = UnixDate((long?)directory.Attribute("scannedAt"))
            });
        }
        return result;
    }

    internal static PlexContainerPage ParseContainerPage(string contentType, string body, int? totalSizeHeader)
    {
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || body.TrimStart().StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var mc = root.TryGetProperty("MediaContainer", out var mediaContainer) ? mediaContainer : root;
            var total = JsonInt(mc, "totalSize") ?? totalSizeHeader ?? JsonInt(mc, "size");
            var items = new List<PlexMediaPreview>();
            if (mc.TryGetProperty("Metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in metadata.EnumerateArray())
                {
                    var title = JsonStr(item, "title") ?? "Untitled";
                    var parent = JsonStr(item, "grandparentTitle") ?? JsonStr(item, "parentTitle");
                    items.Add(new PlexMediaPreview
                    {
                        Title = parent ?? title,
                        Subtitle = parent is null ? YearLabel(JsonInt(item, "year")) : title,
                        ThumbPath = JsonStr(item, "grandparentThumb") ?? JsonStr(item, "thumb"),
                        AddedAt = UnixDate(JsonLong(item, "addedAt"))
                    });
                }
            }
            return new PlexContainerPage(total, items);
        }

        var xml = XDocument.Parse(body);
        var rootXml = xml.Root?.Name.LocalName == "MediaContainer" ? xml.Root : xml.Root?.Element("MediaContainer") ?? xml.Root;
        var totalXml = (int?)rootXml?.Attribute("totalSize") ?? totalSizeHeader ?? (int?)rootXml?.Attribute("size");
        var previews = (rootXml?.Elements() ?? Enumerable.Empty<XElement>())
            .Where(x => x.Name.LocalName is "Video" or "Directory" or "Track")
            .Select(item =>
            {
                var title = (string?)item.Attribute("title") ?? "Untitled";
                var parent = (string?)item.Attribute("grandparentTitle") ?? (string?)item.Attribute("parentTitle");
                return new PlexMediaPreview
                {
                    Title = parent ?? title,
                    Subtitle = parent is null ? YearLabel((int?)item.Attribute("year")) : title,
                    ThumbPath = (string?)item.Attribute("grandparentThumb") ?? (string?)item.Attribute("thumb"),
                    AddedAt = UnixDate((long?)item.Attribute("addedAt"))
                };
            }).ToList();
        return new PlexContainerPage(totalXml, previews);
    }

    private static MediaType PlexMediaType(string? plexType) => plexType switch
    {
        "show" => MediaType.TvShow,
        "artist" => MediaType.Music,
        _ => MediaType.Movie
    };

    private static string? YearLabel(int? year) => year is > 0 ? year.Value.ToString() : null;
    private static DateTime? UnixDate(long? seconds) => seconds is > 0
        ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value).UtcDateTime
        : null;
    internal sealed record PlexContainerPage(int? TotalSize, List<PlexMediaPreview> Items);

    public Task<List<MediaCardDto>> GetLibraryContentAsync(MediaType mediaType, int page = 1, int pageSize = 20)
        => _metadata.GetLibraryAsync(mediaType, page, pageSize); // Will be replaced with Plex library calls

    public Task<MediaDetailDto?> GetMediaDetailsAsync(int mediaId, MediaType mediaType)
        => _metadata.GetDetailsAsync(mediaId, mediaType);

    public Task<MediaDetailDto?> GetMediaDetailsAsync(MediaRef mediaRef)
        => _metadata.GetDetailsAsync(mediaRef);

    // Episodes of a season with per-episode "already on Plex" overlaid from the DB index.
    public async Task<List<EpisodeDto>> GetSeasonEpisodesAsync(int showId, int seasonNumber)
    {
        var episodes = await _metadata.GetSeasonEpisodesAsync(showId, seasonNumber);
        if (episodes.Count == 0) return episodes;
        var ratingKey = await _seasonEvaluator.ResolveRatingKeyAsync(showId);
        if (!string.IsNullOrEmpty(ratingKey))
        {
            var row = await _db.PlexSeasonAvailability
                .Where(s => s.ShowRatingKey == ratingKey && s.SeasonNumber == seasonNumber)
                .Select(s => new { s.AvailableEpisodesCsv, s.EpisodeQualityJson }).FirstOrDefaultAsync();
            if (row is not null && !string.IsNullOrEmpty(row.AvailableEpisodesCsv))
            {
                var have = row.AvailableEpisodesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => int.TryParse(x, out var n) ? n : -1).ToHashSet();
                var quality = DeserializeEpisodeQuality(row.EpisodeQualityJson);
                foreach (var ep in episodes)
                {
                    ep.IsAvailable = have.Contains(ep.EpisodeNumber);
                    if (ep.IsAvailable && quality.TryGetValue(ep.EpisodeNumber, out var q)) ep.MediaQuality = q;
                }
            }
        }
        return episodes;
    }

    private static Dictionary<int, MediaQualityDto> DeserializeEpisodeQuality(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<int, MediaQualityDto>>(json, QualityJsonOpts) ?? new(); }
        catch { return new(); }
    }

    // Per-season dominant quality summary for a show (best resolution/codec + total size across the
    // season's episodes). Used to render season-level and series-level quality chips.
    public async Task<Dictionary<int, MediaQualityDto>> GetSeasonQualitySummariesAsync(int tvShowId)
    {
        var result = new Dictionary<int, MediaQualityDto>();
        var ratingKey = await _seasonEvaluator.ResolveRatingKeyAsync(tvShowId);
        if (string.IsNullOrEmpty(ratingKey)) return result;
        var rows = await _db.PlexSeasonAvailability.AsNoTracking()
            .Where(s => s.ShowRatingKey == ratingKey)
            .Select(s => new { s.SeasonNumber, s.EpisodeQualityJson }).ToListAsync();
        foreach (var r in rows)
        {
            var qmap = DeserializeEpisodeQuality(r.EpisodeQualityJson);
            if (qmap.Count == 0) continue;
            var summary = AggregateQuality(qmap.Values.ToList());
            if (summary is not null) result[r.SeasonNumber] = summary;
        }
        return result;
    }

    // Which seasons of a TMDB show are FULLY complete on Plex (Plex episode count >= TMDB's expected
    // count for that season) — not merely "has at least one episode". Delegates to the shared evaluator
    // so this definition can never drift out of sync with the fulfillment queue's missing-target logic.
    public Task<List<int>> GetAvailableSeasonsAsync(int tvShowId) => _seasonEvaluator.GetCompleteSeasonsAsync(tvShowId);

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

    public Task<List<MediaCardDto>> GetSimilarAsync(MediaRef mediaRef, int count = 12)
        => _metadata.GetSimilarAsync(mediaRef, count);

    public Task<List<MediaCardDto>> SearchMediaAsync(string query, MediaType? mediaType = null)
        => _metadata.SearchAsync(query, mediaType);

    public Task<List<MediaCardDto>> SearchMediaAsync(MediaSearchQuery query)
        => _metadata.SearchAsync(query);

    public async Task AnnotateAvailabilityAsync(List<MediaCardDto> items)
    {
        if (items == null || items.Count == 0) return;
        if (string.IsNullOrWhiteSpace(_cfg.PrimaryServerUrl) || string.IsNullOrWhiteSpace(_cfg.ServerToken)) return;

        try
        {
            var idx = await EnsureAvailabilityIndexAsync();
        foreach (var it in items)
        {
            // Music is reconciled by artist/album identity in the later import pipeline. Applying the
            // video title/year fallback here can falsely mark an album as a same-named movie.
            if (it.MediaType == MediaType.Music) continue;
            if (it.IsAvailable) continue; // already known available
            var matched = false;
            // Try external ids first
            string? matchPath = null;
            string? rk = null;
            if (it.TmdbId is int tmdb && idx.ByExternal.TryGetValue($"tmdb:{tmdb}", out var rkTmdb)) { matched = true; rk = rkTmdb; matchPath = $"guid:tmdb:{tmdb}"; }
            else if (!string.IsNullOrEmpty(it.ImdbId) && idx.ByExternal.TryGetValue($"imdb:{it.ImdbId}", out var rkImdb)) { matched = true; rk = rkImdb; matchPath = $"guid:imdb:{it.ImdbId}"; }
            else if (it.TvdbId is int tvdb && idx.ByExternal.TryGetValue($"tvdb:{tvdb}", out var rkTvdb)) { matched = true; rk = rkTvdb; matchPath = $"guid:tvdb:{tvdb}"; }
            else if (!matched && it.TmdbId is null && it.MediaType == MediaType.Movie && it.Id > 0 && idx.ByExternal.TryGetValue($"tmdb:{it.Id}", out var rkFallback)) { matched = true; rk = rkFallback; matchPath = $"guid:tmdb:{it.Id}(fallback-from-Id)"; }
            // Fallback: title+year +/- 1 year
            if (!matched && it.Year is int y)
            {
                foreach (var yr in new[] { y - 1, y, y + 1 })
                {
                    var key = NormalizeTitleYear(it.Title, yr);
                    if (idx.ByTitleYear.Contains(key))
                    {
                        matched = true;
                        matchPath = $"title-year:{yr}";
                        idx.ByTitleYearKey.TryGetValue(key, out rk);
                        break;
                    }
                }
            }
            if (matched)
            {
                it.IsAvailable = true;
                if (!string.IsNullOrEmpty(rk))
                {
                    it.PlexUrl = await BuildPlexWebUrlAsync(rk);
                    if (idx.ByRatingKeyQuality.TryGetValue(rk, out var q))
                    {
                        it.MediaQuality = q;
                        it.Quality ??= q.Resolution;
                    }
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

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Availability index and helpers
    private record AvailabilityIndex(HashSet<string> ByTitleYear, Dictionary<string, string> ByTitleYearKey, Dictionary<string, string> ByExternal, Dictionary<string, MediaQualityDto> ByRatingKeyQuality, DateTime BuiltAt);
    private const string AvailabilityCacheKey = "plex_availability_index";
    // Last-rebuild stats live in the (singleton) memory cache rather than an instance field, since
    // PlexApiService is resolved fresh per DI scope — the background refresh service and an admin
    // page load run in different scopes and would otherwise never see each other's state.
    private record LastRebuildResult(int Maps, int Seasons, int Episodes, int PrunedMaps, int PrunedSeasons, DateTime At);
    private const string LastRebuildCacheKey = "plex_last_rebuild_result";
    // Fast read path: the in-memory match index is projected FROM THE DB (kept fresh by the
    // background AvailabilityRefreshService). This never hits Plex, so annotating a page is cheap.
    private async Task<AvailabilityIndex> EnsureAvailabilityIndexAsync()
    {
        if (_cache.TryGetValue<AvailabilityIndex>(AvailabilityCacheKey, out var cached) &&
            cached is not null &&
            (DateTime.UtcNow - cached.BuiltAt) < TimeSpan.FromMinutes(5))
            return cached;

        var byTitleYear = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byTitleYearKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byExternal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byRatingKeyQuality = new Dictionary<string, MediaQualityDto>(StringComparer.Ordinal);

        var rows = await _db.PlexMappings.AsNoTracking()
            .Select(m => new { m.ExternalKey, m.RatingKey, m.Title, m.Year, m.VideoResolution, m.VideoCodec, m.AudioCodec, m.Bitrate, m.FileSizeBytes, m.VersionCount })
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
            if (!string.IsNullOrEmpty(m.RatingKey) && !byRatingKeyQuality.ContainsKey(m.RatingKey)
                && (!string.IsNullOrEmpty(m.VideoResolution) || !string.IsNullOrEmpty(m.VideoCodec) || m.Bitrate is not null || m.FileSizeBytes is not null))
            {
                byRatingKeyQuality[m.RatingKey] = new MediaQualityDto
                {
                    Resolution = m.VideoResolution,
                    VideoCodec = m.VideoCodec,
                    AudioCodec = m.AudioCodec,
                    Bitrate = m.Bitrate,
                    FileSizeBytes = m.FileSizeBytes,
                    VersionCount = m.VersionCount < 1 ? 1 : m.VersionCount
                };
            }
        }

        var index = new AvailabilityIndex(byTitleYear, byTitleYearKey, byExternal, byRatingKeyQuality, DateTime.UtcNow);
        _cache.Set(AvailabilityCacheKey, index, TimeSpan.FromMinutes(5));
        return index;
    }

    // Expensive write path: scan Plex and upsert the availability tables (item id-maps + per-season
    // episode presence), then prune anything not seen this pass (removed from the server). Called by
    // the background refresh service and the manual rebuild endpoint.
    public async Task<object> RebuildAvailabilityFromPlexAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_cfg.PrimaryServerUrl) || string.IsNullOrWhiteSpace(_cfg.ServerToken))
            return new { skipped = true, reason = "Plex not configured" };

        var scanStart = DateTime.UtcNow;
        int maps = 0, episodes = 0, seasons = 0;
        var libraries = await GetLibrariesAsync();
        _logger.LogInformation("Plex availability scan: {LibCount} libraries", libraries.Count);

        // Preload existing rows ONCE into dictionaries (tracked) so the loop matches in memory instead
        // of a SELECT per guid/season. New rows are Added; existing rows are mutated in-place.
        var mapByKey = await _db.PlexMappings.ToDictionaryAsync(m => m.ExternalKey, ct);
        var seasonRows = await _db.PlexSeasonAvailability.ToListAsync(ct);
        var seasonByKey = seasonRows.ToDictionary(s => (s.ShowRatingKey, s.SeasonNumber));

        foreach (var lib in libraries)
        {
            if (lib.Type != Shared.Enums.MediaType.Movie && lib.Type != Shared.Enums.MediaType.TvShow) continue;

            // Items (movies + shows) -> external id -> ratingKey mappings.
            await foreach (var item in EnumerateLibraryItemsAsync(lib.Key))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var guid in item.guids)
                {
                    var (type, id) = ParseGuid(guid);
                    if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(id)) continue;
                    var key = $"{type}:{id}";
                    if (!mapByKey.TryGetValue(key, out var existing))
                    {
                        existing = new PlexMappingEntity { ExternalKey = key };
                        _db.PlexMappings.Add(existing);
                        mapByKey[key] = existing;
                    }
                    existing.RatingKey = item.ratingKey; existing.MediaType = lib.Type; existing.Title = item.title; existing.Year = item.year; existing.LastSeenAt = scanStart; existing.MissedScans = 0;
                    // Quality columns apply to movies (a show container item has no media file of its own).
                    existing.VideoResolution = item.quality?.Resolution;
                    existing.VideoCodec = item.quality?.VideoCodec;
                    existing.AudioCodec = item.quality?.AudioCodec;
                    existing.Bitrate = item.quality?.Bitrate;
                    existing.FileSizeBytes = item.quality?.FileSizeBytes;
                    existing.VersionCount = item.quality?.VersionCount ?? 1;
                    maps++;
                }
            }

            // Episodes -> per-season presence (TV libraries only). One type=4 query returns every
            // episode with its show (grandparentRatingKey) + season (parentIndex) + number (index).
            if (lib.Type == Shared.Enums.MediaType.TvShow)
            {
                var perSeason = new Dictionary<(string show, int season), SortedSet<int>>();
                var perSeasonQuality = new Dictionary<(string show, int season), Dictionary<int, MediaQualityDto>>();
                await foreach (var ep in EnumerateEpisodesAsync(lib.Key))
                {
                    ct.ThrowIfCancellationRequested();
                    var k = (ep.showRatingKey, ep.season);
                    if (!perSeason.TryGetValue(k, out var set)) { set = new SortedSet<int>(); perSeason[k] = set; }
                    if (ep.episode > 0) set.Add(ep.episode);
                    if (ep.episode > 0 && ep.quality is not null)
                    {
                        if (!perSeasonQuality.TryGetValue(k, out var qmap)) { qmap = new(); perSeasonQuality[k] = qmap; }
                        qmap[ep.episode] = ep.quality;
                    }
                    episodes++;
                }
                foreach (var (k, set) in perSeason)
                {
                    var csv = string.Join(",", set);
                    if (!seasonByKey.TryGetValue((k.show, k.season), out var row))
                    {
                        row = new PlexSeasonAvailabilityEntity { ShowRatingKey = k.show, SeasonNumber = k.season };
                        _db.PlexSeasonAvailability.Add(row);
                        seasonByKey[(k.show, k.season)] = row;
                    }
                    row.AvailableEpisodesCsv = csv; row.EpisodeCount = set.Count; row.LastSeenAt = scanStart; row.MissedScans = 0;
                    row.EpisodeQualityJson = perSeasonQuality.TryGetValue(k, out var qmap) && qmap.Count > 0
                        ? JsonSerializer.Serialize(qmap, QualityJsonOpts)
                        : null;
                    seasons++;
                }
            }
        }
        await _db.SaveChangesAsync(ct);

        // ---- Prune, defensively -------------------------------------------------------------------
        // Rows not touched this pass *might* mean "removed from the server" — or the scan itself might have
        // been partial (Plex restarting, a section timing out, a token blip). Deleting unconditionally on the
        // second reading was catastrophic: the index emptied, every season looked missing, and the next
        // monitor/enqueue pass mass-requeued downloads of content already on disk. So: refuse to prune at all
        // when the scan looks untrustworthy, and otherwise require a row to be missing several passes running.
        var prior = _cache.Get<LastRebuildResult>(LastRebuildCacheKey);
        var suspicious =
            libraries.Count == 0 ? "Plex returned no libraries"
            : maps == 0 ? "the scan matched no library items"
            : prior is not null && prior.Maps > 0 && maps < prior.Maps * (1 - MaxItemDropFractionBeforeSkippingPrune)
                ? $"item count fell from {prior.Maps} to {maps} (>{MaxItemDropFractionBeforeSkippingPrune:P0} drop)"
                : null;

        int prunedMaps = 0, prunedSeasons = 0, agedMaps = 0, agedSeasons = 0;
        if (suspicious is not null)
        {
            _logger.LogWarning("Plex availability scan looks partial ({Reason}); keeping every existing row rather than pruning", suspicious);
        }
        else
        {
            var staleMaps = await _db.PlexMappings.Where(m => m.LastSeenAt < scanStart).ToListAsync(ct);
            var staleSeasons = await _db.PlexSeasonAvailability.Where(s => s.LastSeenAt < scanStart).ToListAsync(ct);

            foreach (var m in staleMaps)
            {
                if (++m.MissedScans >= MissedScansBeforePrune) { _db.PlexMappings.Remove(m); prunedMaps++; }
                else agedMaps++;
            }
            foreach (var s in staleSeasons)
            {
                if (++s.MissedScans >= MissedScansBeforePrune) { _db.PlexSeasonAvailability.Remove(s); prunedSeasons++; }
                else agedSeasons++;
            }
            await _db.SaveChangesAsync(ct);
            if (agedMaps > 0 || agedSeasons > 0)
                _logger.LogInformation("Availability prune: {AgedMaps} map(s) / {AgedSeasons} season(s) missing this pass but kept (need {Needed} consecutive misses)",
                    agedMaps, agedSeasons, MissedScansBeforePrune);
        }

        _cache.Remove(AvailabilityCacheKey); // force the read projection to rebuild from fresh DB
        _cache.Set(LastRebuildCacheKey, new LastRebuildResult(maps, seasons, episodes, prunedMaps, prunedSeasons, scanStart), TimeSpan.FromDays(30));
        _logger.LogInformation("Plex availability rebuilt: {Maps} id-maps, {Seasons} seasons, {Eps} episodes; pruned {PMaps} maps / {PSeasons} seasons",
            maps, seasons, episodes, prunedMaps, prunedSeasons);
        return new { maps, seasons, episodes, prunedMaps, prunedSeasons, prunePolicy = suspicious ?? "normal", at = scanStart };
    }

    // A row must be absent from this many consecutive scans before it's deleted, and a scan that loses more
    // than this fraction of its item count versus the previous one is treated as partial and prunes nothing.
    // Constants for now; these become admin-configurable alongside the other monitoring knobs.
    private const int MissedScansBeforePrune = 3;
    private const double MaxItemDropFractionBeforeSkippingPrune = 0.30;

    // Enumerate every episode in a TV library section via a single paged type=4 query.
    private async IAsyncEnumerable<(string showRatingKey, int season, int episode, MediaQualityDto? quality)> EnumerateEpisodesAsync(string sectionKey)
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
                            yield return (show, season.Value, episode ?? -1, ExtractQuality(m));
                    }
                }
            }
            if (returned < size) yield break;
            start += size;
        }
    }

    private static string JsonStringOrNumber(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var p)) return string.Empty;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString() ?? string.Empty,
            JsonValueKind.Number => p.GetInt64().ToString(),
            _ => string.Empty
        };
    }

    private static int? JsonInt(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n)) return n;
        if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var s)) return s;
        return null;
    }

    private static long? JsonLong(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n)) return n;
        if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var s)) return s;
        return null;
    }

    private static bool JsonBool(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var p)) return false;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number when p.TryGetInt32(out var n) => n != 0,
            JsonValueKind.String when bool.TryParse(p.GetString(), out var b) => b,
            JsonValueKind.String when int.TryParse(p.GetString(), out var n) => n != 0,
            _ => false
        };
    }

    private static string? JsonStr(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.ToString(),
            _ => null
        };
    }

    // ---- Media quality parsing (Media/Part arrays returned by the Plex library endpoints) ----
    // JSON options for the per-episode quality blob stored on PlexSeasonAvailabilityEntity.
    private static readonly JsonSerializerOptions QualityJsonOpts = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    // Rank a resolution label so we can pick the "best" version and aggregate a season/show.
    private static int ResolutionRank(string? res) => res switch
    {
        "8K" => 5, "4K" => 4, "1080p" => 3, "720p" => 2, "576p" => 1, "480p" => 1, _ => 0
    };

    private static string? NormalizeResolution(string? raw, int? width, int? height)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "8k": return "8K";
            case "4k": return "4K";
            case "1080": return "1080p";
            case "720": return "720p";
            case "576": return "576p";
            case "480": return "480p";
            case "sd": return "SD";
        }
        // Fall back to pixel dimensions when videoResolution is absent or unrecognized.
        int w = width ?? 0, h = height ?? 0;
        if (w >= 7000 || h >= 4000) return "8K";
        if (w >= 3000 || h >= 1700) return "4K";
        if (w >= 1900 || h >= 1000) return "1080p";
        if (w >= 1200 || h >= 700) return "720p";
        if (w > 0 || h > 0) return "SD";
        var r = raw?.Trim();
        return string.IsNullOrEmpty(r) ? null : r.ToUpperInvariant();
    }

    private static string? NormalizeVideoCodec(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "hevc" or "h265" or "x265" => "HEVC",
        "h264" or "avc" or "x264" => "H.264",
        "mpeg4" => "MPEG-4",
        "mpeg2video" or "mpeg2" => "MPEG-2",
        "vp9" => "VP9",
        "av1" => "AV1",
        var s => s!.ToUpperInvariant()
    };

    private static string? NormalizeAudioCodec(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "eac3" or "ec-3" => "EAC3",
        "ac3" => "AC3",
        "dca" or "dts" => "DTS",
        "truehd" => "TrueHD",
        "aac" => "AAC",
        "flac" => "FLAC",
        "mp3" => "MP3",
        "opus" => "Opus",
        var s => s!.ToUpperInvariant()
    };

    // Extract quality from a Plex metadata item's Media[]/Part[] arrays. Picks the highest-ranked
    // version (by resolution, then bitrate); VersionCount reflects how many Media entries exist.
    // Returns null when the item carries no media (e.g. a TV show container item) or no Media array.
    private static MediaQualityDto? ExtractQuality(JsonElement item)
    {
        if (!item.TryGetProperty("Media", out var mediaArr) || mediaArr.ValueKind != JsonValueKind.Array) return null;
        MediaQualityDto? best = null;
        int count = 0;
        foreach (var media in mediaArr.EnumerateArray())
        {
            if (media.ValueKind != JsonValueKind.Object) continue;
            count++;
            long size = 0;
            if (media.TryGetProperty("Part", out var partArr) && partArr.ValueKind == JsonValueKind.Array)
                foreach (var part in partArr.EnumerateArray())
                    size += JsonLong(part, "size") ?? 0;
            var info = new MediaQualityDto
            {
                Resolution = NormalizeResolution(JsonStr(media, "videoResolution"), JsonInt(media, "width"), JsonInt(media, "height")),
                VideoCodec = NormalizeVideoCodec(JsonStr(media, "videoCodec")),
                AudioCodec = NormalizeAudioCodec(JsonStr(media, "audioCodec")),
                Bitrate = JsonInt(media, "bitrate"),
                FileSizeBytes = size > 0 ? size : null,
                VersionCount = 1
            };
            if (best is null
                || ResolutionRank(info.Resolution) > ResolutionRank(best.Resolution)
                || (ResolutionRank(info.Resolution) == ResolutionRank(best.Resolution) && (info.Bitrate ?? 0) > (best.Bitrate ?? 0)))
                best = info;
        }
        if (best is null || count == 0) return null;
        best.VersionCount = count;
        return best;
    }

    // Combine several files (e.g. a season's episodes) into one representative summary:
    // best resolution/codec seen + total size. VersionCount stays 1 (these are distinct items,
    // not alternate versions of the same title).
    private static MediaQualityDto? AggregateQuality(IReadOnlyCollection<MediaQualityDto> items)
    {
        if (items.Count == 0) return null;
        MediaQualityDto best = items.First();
        long totalSize = 0;
        foreach (var q in items)
        {
            totalSize += q.FileSizeBytes ?? 0;
            if (ResolutionRank(q.Resolution) > ResolutionRank(best.Resolution)
                || (ResolutionRank(q.Resolution) == ResolutionRank(best.Resolution) && (q.Bitrate ?? 0) > (best.Bitrate ?? 0)))
                best = q;
        }
        return new MediaQualityDto
        {
            Resolution = best.Resolution,
            VideoCodec = best.VideoCodec,
            AudioCodec = best.AudioCodec,
            Bitrate = best.Bitrate,
            FileSizeBytes = totalSize > 0 ? totalSize : null,
            VersionCount = 1
        };
    }

    // Rebuild a MediaQualityDto from the denormalized columns stored on a movie's PlexMappingEntity.
    private static MediaQualityDto? QualityFromMapping(PlexMappingEntity m)
    {
        if (string.IsNullOrEmpty(m.VideoResolution) && string.IsNullOrEmpty(m.VideoCodec)
            && m.Bitrate is null && m.FileSizeBytes is null) return null;
        return new MediaQualityDto
        {
            Resolution = m.VideoResolution,
            VideoCodec = m.VideoCodec,
            AudioCodec = m.AudioCodec,
            Bitrate = m.Bitrate,
            FileSizeBytes = m.FileSizeBytes,
            VersionCount = m.VersionCount < 1 ? 1 : m.VersionCount
        };
    }

    private static string NormalizeTitleYear(string? title, int year)
    {
        if (string.IsNullOrWhiteSpace(title)) return year.ToString();
        var t = new string(title.Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch)).ToArray()).Trim().ToLowerInvariant();
        t = string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return $"{t}|{year}";
    }

    private (string? type, string? id) ParseGuid(string guid)
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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse Plex guid {Guid}", guid);
            return (null, null);
        }
    }

    // The machine id is parsed as a side effect of GetServerInfoAsync (which hits the same root "/"
    // endpoint); this just triggers that call once and reuses the cached value afterwards.
    private async Task<string?> EnsureServerMachineIdAsync()
    {
        if (!string.IsNullOrEmpty(_serverMachineId)) return _serverMachineId;
        await GetServerInfoAsync();
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

    private async IAsyncEnumerable<(string ratingKey, string? title, int? year, List<string> guids, MediaQualityDto? quality)> EnumerateLibraryItemsAsync(string sectionKey)
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
                            yield return (ratingKey, title, year, guids, ExtractQuality(m));
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
                        // XML fallback path: quality isn't parsed here (Plex serves JSON when we ask for it).
                        yield return (rk, title, year, guids, null);
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
        try
        {
            var res = await _http.SendAsync(req);
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Plex sections/raw request failed at {Url}", _cfg.PrimaryServerUrl);
            throw;
        }
    }

    public async Task<object> GetMetadataAsync(string ratingKey)
    {
        var baseUrl = NormalizeBaseUrl(_cfg.PrimaryServerUrl) ?? throw new InvalidOperationException("No Plex base URL");
        var token = _cfg.ServerToken ?? string.Empty;
        var url = $"{baseUrl}/library/metadata/{Uri.EscapeDataString(ratingKey)}?X-Plex-Token={Uri.EscapeDataString(token)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureDefaultHeaders(req.Headers);
        string text;
        try
        {
            var res = await _http.SendAsync(req);
            res.EnsureSuccessStatusCode();
            text = await res.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Plex metadata lookup failed for ratingKey {RatingKey}", ratingKey);
            throw;
        }
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
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to parse Plex metadata response as JSON for ratingKey {RatingKey}; trying XML", ratingKey); }
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
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to parse Plex metadata response as XML for ratingKey {RatingKey}", ratingKey); }
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
        string text;
        try
        {
            var res = await _http.SendAsync(req);
            res.EnsureSuccessStatusCode();
            text = await res.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Plex server search failed for {Query}", query);
            throw;
        }
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
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to parse Plex search response as JSON for {Query}; trying XML", query); }
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
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to parse Plex search response as XML for {Query}", query); }
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

    public async Task<List<PlexSessionInfo>> GetActiveSessionsAsync()
    {
        if (string.IsNullOrWhiteSpace(_cfg.PrimaryServerUrl) || string.IsNullOrWhiteSpace(_cfg.ServerToken))
            return new();

        try
        {
            var baseUrl = NormalizeBaseUrl(_cfg.PrimaryServerUrl);
            if (baseUrl is null) return new();
            var response = await SendPlexAsync(baseUrl + "/status/sessions", 0, 100);
            return response is null ? new() : ParseSessions(response.Value.ContentType, response.Value.Body);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Plex server unreachable while fetching active sessions");
            return new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Plex active sessions");
            return new();
        }
    }

    internal static List<PlexSessionInfo> ParseSessions(string contentType, string body)
    {
        var list = new List<PlexSessionInfo>();
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || body.TrimStart().StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var mc = root.TryGetProperty("MediaContainer", out var mediaContainer) ? mediaContainer : root;
            if (!mc.TryGetProperty("Metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in metadata.EnumerateArray())
            {
                var duration = JsonLong(item, "duration") ?? 0;
                var offset = JsonLong(item, "viewOffset") ?? 0;
                var episodeTitle = JsonStr(item, "title") ?? "Unknown title";
                var seriesTitle = JsonStr(item, "grandparentTitle");
                var season = JsonInt(item, "parentIndex");
                var episode = JsonInt(item, "index");
                var isTranscoding = item.TryGetProperty("TranscodeSession", out _);
                item.TryGetProperty("User", out var user);
                item.TryGetProperty("Player", out var player);
                item.TryGetProperty("Session", out var session);
                var videoDecision = FirstMediaString(item, "videoDecision");
                var audioDecision = FirstMediaString(item, "audioDecision");
                var playbackMode = isTranscoding || videoDecision == "transcode" || audioDecision == "transcode"
                    ? "Transcode"
                    : videoDecision == "copy" || audioDecision == "copy" ? "Direct Stream" : "Direct Play";

                list.Add(new PlexSessionInfo
                {
                    Title = seriesTitle ?? episodeTitle,
                    Subtitle = seriesTitle is not null
                        ? $"{EpisodeCode(season, episode)}{episodeTitle}"
                        : YearLabel(JsonInt(item, "year")),
                    Username = JsonStr(user, "title") ?? "Unknown user",
                    UserThumbPath = JsonStr(user, "thumb"),
                    ThumbPath = JsonStr(item, "grandparentThumb") ?? JsonStr(item, "thumb"),
                    ArtPath = JsonStr(item, "grandparentArt") ?? JsonStr(item, "art"),
                    ProgressPercent = duration > 0 ? (int)Math.Clamp(offset * 100 / duration, 0, 100) : 0,
                    PositionMilliseconds = offset,
                    DurationMilliseconds = duration,
                    IsTranscoding = playbackMode == "Transcode",
                    PlaybackMode = playbackMode,
                    BitrateKbps = JsonInt(session, "bandwidth"),
                    PlayerName = JsonStr(player, "title"),
                    Device = JsonStr(player, "device") ?? JsonStr(player, "product"),
                    Platform = JsonStr(player, "platform"),
                    Location = JsonStr(session, "location"),
                    State = JsonStr(player, "state") ?? "playing"
                });
            }
            return list;
        }

        var xml = XDocument.Parse(body);
        var xmlRoot = xml.Root?.Name.LocalName == "MediaContainer" ? xml.Root : xml.Root?.Element("MediaContainer") ?? xml.Root;
        foreach (var item in xmlRoot?.Elements("Video") ?? Enumerable.Empty<XElement>())
        {
            var duration = (long?)item.Attribute("duration") ?? 0;
            var offset = (long?)item.Attribute("viewOffset") ?? 0;
            var seriesTitle = (string?)item.Attribute("grandparentTitle");
            var episodeTitle = (string?)item.Attribute("title") ?? "Unknown title";
            var player = item.Element("Player");
            var session = item.Element("Session");
            var isTranscoding = item.Element("TranscodeSession") is not null;
            list.Add(new PlexSessionInfo
            {
                Title = seriesTitle ?? episodeTitle,
                Subtitle = seriesTitle is not null
                    ? $"{EpisodeCode((int?)item.Attribute("parentIndex"), (int?)item.Attribute("index"))}{episodeTitle}"
                    : YearLabel((int?)item.Attribute("year")),
                Username = (string?)item.Element("User")?.Attribute("title") ?? "Unknown user",
                UserThumbPath = (string?)item.Element("User")?.Attribute("thumb"),
                ThumbPath = (string?)item.Attribute("grandparentThumb") ?? (string?)item.Attribute("thumb"),
                ArtPath = (string?)item.Attribute("grandparentArt") ?? (string?)item.Attribute("art"),
                ProgressPercent = duration > 0 ? (int)Math.Clamp(offset * 100 / duration, 0, 100) : 0,
                PositionMilliseconds = offset,
                DurationMilliseconds = duration,
                IsTranscoding = isTranscoding,
                PlaybackMode = isTranscoding ? "Transcode" : "Direct Play",
                BitrateKbps = (int?)session?.Attribute("bandwidth"),
                PlayerName = (string?)player?.Attribute("title"),
                Device = (string?)player?.Attribute("device") ?? (string?)player?.Attribute("product"),
                Platform = (string?)player?.Attribute("platform"),
                Location = (string?)session?.Attribute("location"),
                State = (string?)player?.Attribute("state") ?? "playing"
            });
        }
        return list;
    }

    private async Task<PlexUsageInsights> GetUsageInsightsAsync()
    {
        if (_cache.TryGetValue<PlexUsageInsights>(UsageInsightsCacheKey, out var cached) && cached is not null)
            return cached;

        await UsageInsightsLock.WaitAsync();
        try
        {
            if (_cache.TryGetValue<PlexUsageInsights>(UsageInsightsCacheKey, out cached) && cached is not null)
                return cached;

            var insights = await FetchUsageInsightsAsync();
            _cache.Set(UsageInsightsCacheKey, insights, TimeSpan.FromMinutes(10));
            _cache.Set(UsageInsightsLastGoodCacheKey, insights, TimeSpan.FromDays(1));
            return insights;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to refresh Plex usage insights; serving the last good snapshot when available");
            return _cache.Get<PlexUsageInsights>(UsageInsightsLastGoodCacheKey) ?? new PlexUsageInsights();
        }
        finally
        {
            UsageInsightsLock.Release();
        }
    }

    private async Task<PlexUsageInsights> FetchUsageInsightsAsync()
    {
        var baseUrl = NormalizeBaseUrl(_cfg.PrimaryServerUrl) ?? throw new InvalidOperationException("Plex is not configured");
        const int periodDays = 30;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-periodDays).ToUnixTimeSeconds();
        var accountsTask = FetchAccountsAsync(baseUrl);
        var historyTask = FetchHistoryAsync(baseUrl, cutoff);
        await Task.WhenAll(accountsTask, historyTask);

        var accounts = accountsTask.Result;
        var history = historyTask.Result;
        var viewers = history
            .Where(row => !string.IsNullOrWhiteSpace(row.AccountId))
            .GroupBy(row => row.AccountId, StringComparer.Ordinal)
            .Select(group =>
            {
                accounts.TryGetValue(group.Key, out var account);
                return new PlexTopViewer
                {
                    Name = account?.Name ?? $"User {group.Key}",
                    ThumbPath = account?.Thumb,
                    Plays = group.Count()
                };
            })
            .OrderByDescending(viewer => viewer.Plays)
            .ThenBy(viewer => viewer.Name, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        var titles = history
            .Where(row => !string.IsNullOrWhiteSpace(row.Title))
            .GroupBy(row => row.GroupKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var sample = group.First();
                return new PlexTopTitle
                {
                    Title = sample.SeriesTitle ?? sample.Title,
                    Subtitle = sample.SeriesTitle is null ? YearLabel(sample.Year) : "TV series",
                    ThumbPath = sample.SeriesThumb ?? sample.Thumb,
                    Plays = group.Count()
                };
            })
            .OrderByDescending(title => title.Plays)
            .ThenBy(title => title.Title, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        return new PlexUsageInsights
        {
            PeriodDays = periodDays,
            PlayCount = history.Count,
            IsAvailable = true,
            UpdatedAt = DateTime.UtcNow,
            TopViewers = viewers,
            TopTitles = titles
        };
    }

    private async Task<Dictionary<string, PlexAccountRow>> FetchAccountsAsync(string baseUrl)
    {
        var response = await SendPlexAsync(baseUrl + "/accounts", 0, 500);
        if (response is null) return new();
        using var doc = JsonDocument.Parse(response.Value.Body);
        var root = doc.RootElement;
        var mc = root.TryGetProperty("MediaContainer", out var mediaContainer) ? mediaContainer : root;
        if (!mc.TryGetProperty("Account", out var rows) || rows.ValueKind != JsonValueKind.Array) return new();
        var result = new Dictionary<string, PlexAccountRow>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            var id = JsonStringOrNumber(row, "id");
            if (!string.IsNullOrWhiteSpace(id))
                result[id] = new PlexAccountRow(JsonStr(row, "name") ?? $"User {id}", JsonStr(row, "thumb"));
        }
        return result;
    }

    private async Task<List<PlexHistoryRow>> FetchHistoryAsync(string baseUrl, long cutoff)
    {
        const int pageSize = 500;
        var history = new List<PlexHistoryRow>();
        for (var start = 0; ; start += pageSize)
        {
            var url = $"{baseUrl}/status/sessions/history/all?viewedAt%3E={cutoff}&sort=viewedAt%3Adesc";
            var response = await SendPlexAsync(url, start, pageSize);
            if (response is null) throw new HttpRequestException("Plex history query failed");
            using var doc = JsonDocument.Parse(response.Value.Body);
            var root = doc.RootElement;
            var mc = root.TryGetProperty("MediaContainer", out var mediaContainer) ? mediaContainer : root;
            if (!mc.TryGetProperty("Metadata", out var rows) || rows.ValueKind != JsonValueKind.Array) break;
            var returned = 0;
            foreach (var row in rows.EnumerateArray())
            {
                returned++;
                var title = JsonStr(row, "title") ?? string.Empty;
                var seriesTitle = JsonStr(row, "grandparentTitle");
                var ratingKey = JsonStringOrNumber(row, seriesTitle is null ? "ratingKey" : "grandparentRatingKey");
                var groupKey = !string.IsNullOrWhiteSpace(ratingKey)
                    ? ratingKey
                    : $"{seriesTitle ?? title}:{JsonInt(row, "year")}";
                history.Add(new PlexHistoryRow(
                    JsonStringOrNumber(row, "accountID"),
                    groupKey,
                    title,
                    seriesTitle,
                    JsonInt(row, "year"),
                    JsonStr(row, "thumb"),
                    JsonStr(row, "grandparentThumb")));
            }
            var total = JsonInt(mc, "totalSize") ?? response.Value.TotalSizeHeader;
            if (returned < pageSize || total is int knownTotal && history.Count >= knownTotal) break;
        }
        return history;
    }

    private static string FirstMediaString(JsonElement item, string property)
    {
        if (!item.TryGetProperty("Media", out var media) || media.ValueKind != JsonValueKind.Array) return string.Empty;
        foreach (var entry in media.EnumerateArray())
        {
            var value = JsonStr(entry, property);
            if (!string.IsNullOrWhiteSpace(value)) return value.ToLowerInvariant();
        }
        return string.Empty;
    }

    private static string EpisodeCode(int? season, int? episode) => season is int s && episode is int e
        ? $"S{s:00} E{e:00} · "
        : string.Empty;

    private sealed record PlexAccountRow(string Name, string? Thumb);
    private sealed record PlexHistoryRow(string AccountId, string GroupKey, string Title, string? SeriesTitle, int? Year, string? Thumb, string? SeriesThumb);

    public async Task RefreshLibraryAsync(string sectionKey)
    {
        if (string.IsNullOrWhiteSpace(_cfg.PrimaryServerUrl) || string.IsNullOrWhiteSpace(_cfg.ServerToken))
            throw new InvalidOperationException("Plex is not configured.");
        var baseUrl = NormalizeBaseUrl(_cfg.PrimaryServerUrl) ?? throw new InvalidOperationException("Invalid Plex server URL.");
        var url = $"{baseUrl}/library/sections/{Uri.EscapeDataString(sectionKey)}/refresh";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureDefaultHeaders(req.Headers);
        req.Headers.Add("X-Plex-Token", _cfg.ServerToken);
        try
        {
            var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Plex library refresh for section {Section} returned {Status}", sectionKey, res.StatusCode);
                throw new InvalidOperationException($"Plex returned {(int)res.StatusCode}");
            }
            _logger.LogInformation("Triggered Plex library refresh for section {Section}", sectionKey);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Plex server unreachable while refreshing library {Section}", sectionKey);
            throw;
        }
    }

    public async Task<string?> ResolveSectionKeyAsync(MediaType mediaType)
    {
        var cacheKey = $"section-key:{mediaType}";
        if (_cache.TryGetValue<string?>(cacheKey, out var cached)) return cached;

        var libraries = await GetLibrariesAsync();
        // First matching section wins; multi-section-per-type (e.g. a separate 4K library) refreshes
        // only this one for now — a follow-up once library-routing rules also carry a section key.
        var key = libraries.FirstOrDefault(l => l.Type == mediaType)?.Key;
        _cache.Set(cacheKey, key, TimeSpan.FromMinutes(10));
        return key;
    }

    public async Task<PlexAvailabilityStatus> GetAvailabilityStatusAsync()
    {
        var configured = !string.IsNullOrWhiteSpace(_cfg.PrimaryServerUrl) && !string.IsNullOrWhiteSpace(_cfg.ServerToken);
        var idx = await EnsureAvailabilityIndexAsync();
        _cache.TryGetValue<LastRebuildResult>(LastRebuildCacheKey, out var last);
        return new PlexAvailabilityStatus
        {
            Configured = configured,
            LastRebuildAt = last?.At,
            TitleYearCount = idx.ByTitleYear.Count,
            ExternalIdCount = idx.ByExternal.Count,
            LastMaps = last?.Maps ?? 0,
            LastSeasons = last?.Seasons ?? 0,
            LastEpisodes = last?.Episodes ?? 0,
            LastPrunedMaps = last?.PrunedMaps ?? 0,
            LastPrunedSeasons = last?.PrunedSeasons ?? 0
        };
    }
}
