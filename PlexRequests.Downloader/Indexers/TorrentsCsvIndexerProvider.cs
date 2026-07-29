using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// torrents-csv.com — an open, community-run torrent search with a plain JSON API
/// (GET /service/search?q={query}&amp;size=100). No scraping, no Cloudflare, no API key. It has no
/// category metadata, so everything leans on the ranker's title/size/seeder/media-type gates — fine in
/// practice, and its long-tail catalog covers niche content (kids' shows, older titles) the scene-focused
/// sources miss. Magnets are built from the reported info hash.
/// </summary>
public class TorrentsCsvIndexerProvider(HttpClient http, IOptions<IndexerOptions> options, ILogger<TorrentsCsvIndexerProvider> logger)
    : IIndexerProvider
{
    private static readonly string[] Trackers =
    {
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://tracker.openbittorrent.com:6969/announce"
    };

    private readonly HttpClient _http = http;
    private readonly IndexerOptions _opts = options.Value;
    private readonly ILogger<TorrentsCsvIndexerProvider> _logger = logger;

    public string Name => "TorrentsCSV";
    public bool Supports(MediaType mediaType) => mediaType is MediaType.Movie or MediaType.TvShow or MediaType.Anime;

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(FulfillmentJobDto job, CancellationToken ct)
    {
        if (!_opts.TorrentsCsvEnabled || string.IsNullOrWhiteSpace(job.Title)) return Array.Empty<ReleaseCandidate>();

        var queries = new List<string>
        {
            job.MediaType == MediaType.Movie && job.Year is int y ? $"{job.Title} {y}" : job.Title
        };
        if (job.MediaType != MediaType.Movie)
        {
            var seasons = job.RequestedSeasons
                .Concat(job.SeasonTargets.Select(t => t.Season))
                .Concat(job.RequestedEpisodes.Select(e => e.Season))
                .Distinct().OrderBy(s => s).Take(4);
            queries.AddRange(seasons.Select(s => $"{job.Title} season {s}"));
        }

        var byHash = new Dictionary<string, ReleaseCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in queries)
        {
            if (ct.IsCancellationRequested) break;
            TorrentsCsvResponse? resp;
            try
            {
                resp = await _http.GetFromJsonAsync<TorrentsCsvResponse>(
                    $"/service/search?q={Uri.EscapeDataString(q)}&size=100", IndexerJson.Options, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Torrents-CSV request failed for \"{Query}\"", q);
                break; // endpoint unreachable — the other queries will fail the same way
            }
            if (resp?.Torrents is null) continue;

            foreach (var t in resp.Torrents)
            {
                if (string.IsNullOrWhiteSpace(t.Infohash) || string.IsNullOrWhiteSpace(t.Name)) continue;
                byHash.TryAdd(t.Infohash!, new ReleaseCandidate
                {
                    ReleaseName = t.Name!,
                    Magnet = IndexerParsing.BuildMagnet(t.Infohash!, t.Name!, Trackers),
                    InfoHash = t.Infohash,
                    Seeders = t.Seeders ?? 0,
                    Leechers = t.Leechers ?? 0,
                    SizeBytes = t.SizeBytes,
                    Source = Name
                });
            }
        }
        return byHash.Values.ToList();
    }

    private sealed class TorrentsCsvResponse
    {
        public List<TorrentsCsvItem>? Torrents { get; set; }
    }

    private sealed class TorrentsCsvItem
    {
        public string? Infohash { get; set; }
        public string? Name { get; set; }
        public long SizeBytes { get; set; }
        public int? Seeders { get; set; }
        public int? Leechers { get; set; }
    }
}
