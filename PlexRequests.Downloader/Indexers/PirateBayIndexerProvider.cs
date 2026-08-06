using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// The Pirate Bay via its JSON API (apibay): GET /q.php?q={query}&amp;cat={cats}. No scraping and no
/// Cloudflare wall — reliable from datacenter/VPN egress where the HTML-scrape providers get blocked —
/// and each row carries an IMDb id, which upgrades matching from fuzzy title text to the authoritative
/// id check in the ranker. Broad general catalog, which is exactly what the scene-focused sources lack
/// (kids'/preschool shows especially). Magnets are built from the reported info hash.
/// </summary>
public class PirateBayIndexerProvider(HttpClient http, IOptions<IndexerOptions> options, ILogger<PirateBayIndexerProvider> logger)
    : IIndexerImplementation
{
    // apibay reports a zeroed hash + "No results returned" sentinel row when a query matches nothing.
    private const string ZeroHash = "0000000000000000000000000000000000000000";

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
    private readonly ILogger<PirateBayIndexerProvider> _logger = logger;

    public string Key => "PirateBay";

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(IndexerConfigDto indexer, FulfillmentJobDto job, CancellationToken ct)
    {
        if (!_opts.PirateBayEnabled || string.IsNullOrWhiteSpace(job.Title)) return Array.Empty<ReleaseCandidate>();

        // Video categories: 201 Movies, 202 Movies DVDR, 207 HD Movies, 211 4K Movies / 205 TV, 208 HD TV, 212 4K TV.
        var cats = job.MediaType == MediaType.Movie ? "201,202,207,211" : "205,208,212";

        // Each query returns at most ~100 rows, so add a per-season query for season-scoped TV jobs
        // (same pattern as the 1337x provider) to surface packs a busy show pushes out of the first page.
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
            List<ApibayItem>? items;
            try
            {
                items = await _http.GetFromJsonAsync<List<ApibayItem>>(
                    $"/q.php?q={Uri.EscapeDataString(q)}&cat={cats}", IndexerJson.Options, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "PirateBay API request failed for \"{Query}\"", q);
                break; // endpoint unreachable — the other queries will fail the same way
            }
            if (items is null) continue;

            foreach (var t in items)
            {
                if (string.IsNullOrWhiteSpace(t.InfoHash) || t.InfoHash == ZeroHash) continue; // "no results" sentinel
                if (string.IsNullOrWhiteSpace(t.Name)) continue;
                if (t.Category is < 200 or > 299) continue; // defensive: video categories only
                byHash.TryAdd(t.InfoHash!, new ReleaseCandidate
                {
                    ReleaseName = t.Name!,
                    Magnet = IndexerParsing.BuildMagnet(t.InfoHash!, t.Name!, Trackers),
                    InfoHash = t.InfoHash,
                    ImdbId = string.IsNullOrWhiteSpace(t.Imdb) ? null : t.Imdb,
                    Seeders = t.Seeders,
                    Leechers = t.Leechers,
                    SizeBytes = t.Size,
                    IndexerId = indexer.Id,
                    Source = indexer.Name
                });
            }
        }
        return byHash.Values.ToList();
    }

    // apibay returns every field as a string; IndexerJson.Options (snake_case + numbers-from-strings)
    // maps info_hash/seeders/leechers/size/category/imdb directly.
    private sealed class ApibayItem
    {
        public string? Name { get; set; }
        public string? InfoHash { get; set; }
        public int Seeders { get; set; }
        public int Leechers { get; set; }
        public long Size { get; set; }
        public int Category { get; set; }
        public string? Imdb { get; set; }
    }
}
