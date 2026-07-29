using System.Xml.Linq;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// Generic Torznab provider (Jackett / Prowlarr). Torznab is the aggregator ecosystem's standard search
/// API: point one or more configured endpoints here (<see cref="IndexerOptions.Torznab"/>) and every
/// tracker set up in the aggregator becomes searchable with no site-specific scraping code — the practical
/// fix for content the built-in public sources index poorly (kids'/preschool shows especially, which EZTV
/// rarely carries and which need indexers with real TV/Kids category coverage). Queries use the proper
/// capability per media type (t=movie / t=tvsearch) with standard categories (2000 movies, 5000 TV,
/// 5070 TV/Anime), plus a per-season query for season-scoped TV jobs so packs surface.
/// Only results that yield a magnet (magneturl attr, magnet link, or an info hash to build one from)
/// are returned — the download client is magnet-only.
/// </summary>
public class TorznabIndexerProvider(HttpClient http, IOptions<IndexerOptions> options, ILogger<TorznabIndexerProvider> logger)
    : IIndexerProvider
{
    private static readonly XNamespace TorznabNs = "http://torznab.com/schemas/2015/feed";

    // Trackers for magnets built from a bare info hash (some indexers report only the hash).
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
    private readonly ILogger<TorznabIndexerProvider> _logger = logger;

    public string Name => "Torznab";
    public bool Supports(MediaType mediaType) => mediaType is MediaType.Movie or MediaType.TvShow or MediaType.Anime;

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(FulfillmentJobDto job, CancellationToken ct)
    {
        var endpoints = _opts.Torznab.Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Url)).ToList();
        if (endpoints.Count == 0 || string.IsNullOrWhiteSpace(job.Title)) return Array.Empty<ReleaseCandidate>();

        // Dedup across endpoints/queries by info hash (falling back to the magnet URI itself).
        var byKey = new Dictionary<string, ReleaseCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in endpoints)
        {
            foreach (var url in BuildQueryUrls(endpoint, job))
            {
                if (ct.IsCancellationRequested) break;
                string xml;
                try
                {
                    xml = await _http.GetStringAsync(url, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Torznab endpoint \"{Endpoint}\" failed for \"{Title}\"; skipping its remaining queries",
                        endpoint.Name, job.Title);
                    break; // endpoint unreachable — its other queries will fail the same way
                }

                foreach (var c in ParseFeed(xml, endpoint.Name, job.Title))
                    byKey.TryAdd(c.InfoHash ?? c.Magnet, c);
            }
        }
        return byKey.Values.ToList();
    }

    private static IEnumerable<string> BuildQueryUrls(TorznabEndpointOptions endpoint, FulfillmentJobDto job)
    {
        var sep = endpoint.Url.Contains('?') ? "&" : "?";
        string Base(string t, string cat) =>
            $"{endpoint.Url.TrimEnd('/')}{sep}apikey={Uri.EscapeDataString(endpoint.ApiKey)}&t={t}&cat={cat}";

        if (job.MediaType == MediaType.Movie)
        {
            // An id search beats fuzzy text wherever the indexer supports it; the text query still runs
            // as a second net (imdbid support varies per tracker and the aggregator merges either way).
            var imdbDigits = DigitsOf(job.ImdbId);
            if (imdbDigits is not null) yield return Base("movie", "2000") + $"&imdbid={imdbDigits}";
            var q = Base("movie", "2000") + $"&q={Uri.EscapeDataString(job.Title)}";
            if (job.Year is int y) q += $"&year={y}";
            yield return q;
            yield break;
        }

        var cat = job.IsAnime ? "5000,5070" : "5000";
        var text = $"&q={Uri.EscapeDataString(job.Title)}";
        yield return Base("tvsearch", cat) + text; // unscoped — catches complete-series/multi-season packs
        var seasons = job.RequestedSeasons
            .Concat(job.SeasonTargets.Select(t => t.Season))
            .Concat(job.RequestedEpisodes.Select(e => e.Season))
            .Distinct().OrderBy(s => s).Take(4);
        foreach (var s in seasons)
            yield return Base("tvsearch", cat) + text + $"&season={s}";
    }

    private List<ReleaseCandidate> ParseFeed(string xml, string endpointName, string jobTitle)
    {
        var candidates = new List<ReleaseCandidate>();
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var item in doc.Descendants("item"))
            {
                var title = ((string?)item.Element("title"))?.Trim();
                if (string.IsNullOrWhiteSpace(title)) continue;

                string? Attr(string name) => item.Elements(TorznabNs + "attr")
                    .FirstOrDefault(a => string.Equals((string?)a.Attribute("name"), name, StringComparison.OrdinalIgnoreCase))
                    ?.Attribute("value")?.Value;

                var infoHash = Attr("infohash");
                var link = (string?)item.Element("link");
                var magnet = Attr("magneturl");
                if (string.IsNullOrWhiteSpace(magnet) && link?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true)
                    magnet = link;
                if (string.IsNullOrWhiteSpace(magnet) && !string.IsNullOrWhiteSpace(infoHash))
                    magnet = IndexerParsing.BuildMagnet(infoHash!, title!, Trackers);
                if (string.IsNullOrWhiteSpace(magnet)) continue; // .torrent-file-only result; client is magnet-only

                long size = long.TryParse(Attr("size") ?? (string?)item.Element("size"), out var sz) ? sz : 0;
                int? season = int.TryParse(Attr("season"), out var sn) && sn > 0 ? sn : null;
                int? episode = int.TryParse(Attr("episode"), out var ep) && ep > 0 ? ep : null;

                candidates.Add(new ReleaseCandidate
                {
                    ReleaseName = title!,
                    Magnet = magnet!,
                    InfoHash = infoHash,
                    ImdbId = Attr("imdbid") ?? Attr("imdb"),
                    Seeders = IndexerParsing.ParseInt(Attr("seeders")),
                    Leechers = IndexerParsing.ParseInt(Attr("peers") ?? Attr("leechers")),
                    SizeBytes = size,
                    Source = $"Torznab:{endpointName}",
                    Season = season,
                    Episode = episode
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Torznab feed parse failed for endpoint \"{Endpoint}\" (search for \"{Title}\")", endpointName, jobTitle);
        }
        return candidates;
    }

    private static string? DigitsOf(string? imdbId)
    {
        if (string.IsNullOrWhiteSpace(imdbId)) return null;
        var digits = new string(imdbId.Where(char.IsDigit).ToArray()).TrimStart('0');
        return digits.Length == 0 ? null : digits;
    }
}
