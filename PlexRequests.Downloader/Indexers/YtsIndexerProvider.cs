using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// YTS public JSON API (movies): GET /api/v2/list_movies.json?query_term={imdb|title}.
/// Returns torrents with quality + seeds + a hash; we build the magnet from the hash + public trackers.
/// Tries each configured mirror (<see cref="IndexerOptions.YtsBaseUrlsCsv"/>) in order — YTS's public
/// domain has a history of going dark/changing, and previously a single hardcoded dead domain meant
/// silent zero movie coverage with no fallback.
/// </summary>
public class YtsIndexerProvider(HttpClient http, IOptions<IndexerOptions> options, ILogger<YtsIndexerProvider> logger) : IIndexerProvider
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

    public string Name => "YTS";
    public bool Supports(MediaType mediaType) => mediaType is MediaType.Movie;

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(FulfillmentJobDto job, CancellationToken ct)
    {
        var term = string.IsNullOrWhiteSpace(job.ImdbId) ? job.Title : job.ImdbId!;
        var mirrors = (_opts.YtsBaseUrlsCsv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        YtsResponse? resp = null;
        foreach (var mirror in mirrors)
        {
            var url = $"{mirror.TrimEnd('/')}/api/v2/list_movies.json?query_term={Uri.EscapeDataString(term)}&limit=50";
            try
            {
                resp = await _http.GetFromJsonAsync<YtsResponse>(url, IndexerJson.Options, ct);
                break; // first mirror that responds wins
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "YTS mirror {Mirror} unreachable/failed; trying next configured mirror if any", mirror);
            }
        }

        if (resp is null && mirrors.Length > 0)
            _logger.LogWarning("All {Count} configured YTS mirror(s) failed for \"{Title}\" — no movie coverage from YTS this search", mirrors.Length, job.Title);

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
                    Seeders = tor.Seeds,
                    Leechers = tor.Peers,
                    SizeBytes = tor.SizeBytes,
                    Source = Name,
                    QualityLabel = tor.Quality
                });
            }
        }
        return candidates;
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
    }
}
