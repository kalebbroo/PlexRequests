using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// YTS public JSON API (movies): GET /api/v2/list_movies.json?query_term={imdb|title}.
/// Returns torrents with quality + seeds + a hash; we build the magnet from the hash + public trackers.
/// Tries each configured mirror (<see cref="IndexerOptions.YtsBaseUrlsCsv"/>) in order — YTS's public
/// domain has a history of going dark/changing, and previously a single hardcoded dead domain meant
/// silent zero movie coverage with no fallback.
/// </summary>
public class YtsIndexerProvider(HttpClient http, IOptions<IndexerOptions> options, ILogger<YtsIndexerProvider> logger)
    : IIndexerImplementation, IReleaseFeedSource
{
    private readonly HttpClient _http = http;
    private readonly IndexerOptions _opts = options.Value;
    private readonly ILogger<YtsIndexerProvider> _logger = logger;

    private static readonly string[] Trackers =
    {
        "udp://open.demonii.com:1337/announce",
        "udp://tracker.openbittorrent.com:80",
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://tracker.leechers-paradise.org:6969",
        "udp://tracker.coppersurfer.tk:6969",
        "udp://p4p.arenabg.com:1337",
        "udp://tracker.internetwarriors.net:1337",
        "udp://9.rarbg.to:2710/announce"
    };

    public string Key => "YTS";

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(IndexerConfigDto indexer, FulfillmentJobDto job, CancellationToken ct)
    {
        var term = string.IsNullOrWhiteSpace(job.ImdbId) ? job.Title : job.ImdbId!;
        var resp = await FetchFromMirrorsAsync(
            $"/api/v2/list_movies.json?query_term={Uri.EscapeDataString(term)}&limit=50",
            $"search for \"{job.Title}\"",
            ct);

        var movies = resp?.Data?.Movies;
        if (movies is null || movies.Count == 0) return Array.Empty<ReleaseCandidate>();

        var candidates = new List<ReleaseCandidate>();
        foreach (var m in movies)
        {
            foreach (var tor in m.Torrents ?? new())
            {
                if (string.IsNullOrWhiteSpace(tor.Hash)) continue;
                var name = $"{m.TitleLong} {tor.Quality} {tor.Type}".Trim();
                candidates.Add(new ReleaseCandidate
                {
                    ReleaseName = name,
                    Magnet = BuildMagnet(tor.Hash!, name),
                    InfoHash = tor.Hash,
                    ImdbId = m.ImdbCode,
                    Seeders = tor.Seeds,
                    Leechers = tor.Peers,
                    SizeBytes = tor.SizeBytes,
                    IndexerId = indexer.Id,
                    Source = indexer.Name,
                    QualityLabel = tor.Quality
                });
            }
        }
        return candidates;
    }

    public async Task<ReleaseFeedPage> FetchAsync(
        IndexerConfigDto indexer,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var items = new List<CatalogItemDto>();
        const int pageSize = 50;
        const int maxPages = 10;
        for (var page = 1; page <= maxPages; page++)
        {
            var response = await FetchFromMirrorsAsync(
                $"/api/v2/list_movies.json?sort_by=date_added&order_by=desc&limit={pageSize}&page={page}",
                "latest-release ingestion",
                cancellationToken);
            var movies = response.Data?.Movies ?? new();
            foreach (var movie in movies)
            {
                foreach (var torrent in movie.Torrents ?? new())
                {
                    var hash = MagnetUtil.Normalize(torrent.Hash);
                    if (hash is null) continue;
                    var name = $"{movie.TitleLong} {torrent.Quality} {torrent.Type}".Trim();
                    items.Add(new CatalogItemDto
                    {
                        ExternalId = hash,
                        ReleaseName = name,
                        SourceUrl = movie.Url,
                        InfoHash = hash,
                        MagnetUri = BuildMagnet(hash, name),
                        ImdbId = movie.ImdbCode,
                        Seeders = torrent.Seeds,
                        Leechers = torrent.Peers,
                        SizeBytes = torrent.SizeBytes > 0 ? torrent.SizeBytes : null,
                        PublishedAt = torrent.DateUploadedUnix > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(torrent.DateUploadedUnix).UtcDateTime : null,
                        MediaType = MediaType.Movie
                    });
                }
            }
            if (string.IsNullOrWhiteSpace(cursor) || movies.Count < pageSize
                || items.Any(x => string.Equals(x.ExternalId, cursor, StringComparison.OrdinalIgnoreCase)))
                break;
        }
        return ReleaseFeedCursor.Select(items, cursor, limit);
    }

    private async Task<YtsResponse> FetchFromMirrorsAsync(
        string pathAndQuery,
        string operation,
        CancellationToken cancellationToken)
    {
        var mirrors = (_opts.YtsBaseUrlsCsv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (mirrors.Length == 0) throw new InvalidOperationException("No YTS API mirrors are configured.");

        Exception? lastError = null;
        foreach (var mirror in mirrors)
        {
            try
            {
                return await _http.GetFromJsonAsync<YtsResponse>(
                    mirror.TrimEnd('/') + pathAndQuery,
                    IndexerJson.Options,
                    cancellationToken) ?? new YtsResponse();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                _logger.LogWarning(ex, "YTS mirror {Mirror} failed during {Operation}; trying the next mirror",
                    mirror, operation);
            }
        }
        throw new HttpRequestException($"All {mirrors.Length} configured YTS mirrors failed during {operation}.", lastError);
    }

    private static string BuildMagnet(string hash, string name)
    {
        var tr = string.Concat(Trackers.Select(t => "&tr=" + Uri.EscapeDataString(t)));
        return $"magnet:?xt=urn:btih:{hash}&dn={Uri.EscapeDataString(name)}{tr}";
    }

    private sealed class YtsResponse { public YtsData? Data { get; set; } }
    private sealed class YtsData { public List<YtsMovie>? Movies { get; set; } }
    private sealed class YtsMovie
    {
        public string? TitleLong { get; set; }
        public string? ImdbCode { get; set; }
        public string? Url { get; set; }
        public List<YtsTorrent>? Torrents { get; set; }
    }
    private sealed class YtsTorrent
    {
        public string? Hash { get; set; }
        public string? Quality { get; set; }
        public string? Type { get; set; }
        public int Seeds { get; set; }
        public int Peers { get; set; }
        public long SizeBytes { get; set; }
        public long DateUploadedUnix { get; set; }
    }
}
