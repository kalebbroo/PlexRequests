using System.Xml.Linq;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// Nyaa (anime). Uses Nyaa's stable RSS feed (no scraping): each item carries nyaa:infoHash,
/// nyaa:seeders/leechers and nyaa:size, so we build a magnet from the info hash + public trackers.
/// Anime titles are commonly requested as Movie or TvShow (TMDB has no "anime" type), so this runs
/// for all of those — for non-anime titles it simply returns nothing.
/// </summary>
public class NyaaIndexerProvider(HttpClient http, IOptions<IndexerOptions> options, ILogger<NyaaIndexerProvider> logger)
    : IIndexerProvider
{
    private static readonly XNamespace Ns = "https://nyaa.si/xmlns/nyaa";
    private static readonly string[] Trackers =
    {
        "http://nyaa.tracker.wf:7777/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://tracker.openbittorrent.com:6969/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://tracker.torrent.eu.org:451/announce"
    };

    private readonly HttpClient _http = http;
    private readonly IndexerOptions _opts = options.Value;
    private readonly ILogger<NyaaIndexerProvider> _logger = logger;

    public string Name => "Nyaa";
    public bool Supports(MediaType mediaType) => mediaType is MediaType.Anime or MediaType.TvShow or MediaType.Movie;
    public bool AnimeOnly => true;

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(FulfillmentJobDto job, CancellationToken ct)
    {
        if (!_opts.NyaaEnabled || string.IsNullOrWhiteSpace(job.Title)) return Array.Empty<ReleaseCandidate>();

        // c=1_2 = "Anime - English-translated"; sorted by seeders desc.
        var url = $"/?page=rss&q={Uri.EscapeDataString(job.Title)}&c={_opts.NyaaCategory}&f=0&s=seeders&o=desc";
        string xml;
        try
        {
            xml = await _http.GetStringAsync(url, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Nyaa RSS request failed for \"{Title}\"", job.Title);
            return Array.Empty<ReleaseCandidate>();
        }

        List<ReleaseCandidate> candidates = new();
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var item in doc.Descendants("item"))
            {
                var title = (string?)item.Element("title");
                var hash = (string?)item.Element(Ns + "infoHash");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(hash)) continue;

                candidates.Add(new ReleaseCandidate
                {
                    ReleaseName = title!,
                    Magnet = IndexerParsing.BuildMagnet(hash!, title!, Trackers),
                    InfoHash = hash,
                    Seeders = IndexerParsing.ParseInt((string?)item.Element(Ns + "seeders")),
                    Leechers = IndexerParsing.ParseInt((string?)item.Element(Ns + "leechers")),
                    SizeBytes = IndexerParsing.ParseSize((string?)item.Element(Ns + "size")),
                    Source = Name
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nyaa RSS parse failed for \"{Title}\"", job.Title);
        }
        return candidates;
    }
}
