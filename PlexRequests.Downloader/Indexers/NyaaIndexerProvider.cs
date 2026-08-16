using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// Nyaa (anime). Uses Nyaa's stable RSS feed (no scraping): each item carries nyaa:infoHash,
/// nyaa:seeders/leechers and nyaa:size, so we build a magnet from the info hash + public trackers.
/// Anime titles are commonly requested as Movie or TvShow (TMDB has no "anime" type), so this runs
/// for all of those — for non-anime titles it simply returns nothing.
/// </summary>
public class NyaaIndexerProvider(
    HttpClient http,
    IIndexerFetch fetch,
    IOptions<IndexerOptions> options,
    ILogger<NyaaIndexerProvider> logger) : IIndexerImplementation, IReleaseFeedSource
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
    private readonly IIndexerFetch _fetch = fetch;
    private readonly IndexerOptions _opts = options.Value;
    private readonly ILogger<NyaaIndexerProvider> _logger = logger;

    public string Key => "Nyaa";

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(IndexerConfigDto indexer, FulfillmentJobDto job, CancellationToken ct)
    {
        if (!_opts.NyaaEnabled || string.IsNullOrWhiteSpace(job.Title)) return Array.Empty<ReleaseCandidate>();

        // c=1_2 = "Anime - English-translated"; sorted by seeders desc.
        var url = $"/?page=rss&q={Uri.EscapeDataString(job.Title)}&c={_opts.NyaaCategory}&f=0&s=seeders&o=desc";
        var xml = await _fetch.GetStringAsync(_http, indexer, url, ct);

        try
        {
            return ParseItems(xml, indexer, int.MaxValue).Select(x => x.Candidate).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Nyaa RSS parse failed for \"{Title}\"", job.Title);
            throw new InvalidDataException("Nyaa returned an invalid RSS document.", ex);
        }
    }

    public async Task<ReleaseFeedPage> FetchAsync(
        IndexerConfigDto indexer,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var url = $"/?page=rss&c={_opts.NyaaCategory}&f=0";
        var xml = await _fetch.GetStringAsync(_http, indexer, url, cancellationToken);
        IReadOnlyList<FeedItem> feed;
        try { feed = ParseItems(xml, indexer, int.MaxValue); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException("Nyaa returned an invalid RSS document.", ex);
        }

        return ReleaseFeedCursor.Select(feed.Select(x => x.CatalogItem).ToList(), cursor, limit);
    }

    internal static IReadOnlyList<FeedItem> ParseItems(string xml, IndexerConfigDto indexer, int limit)
    {
        var doc = XDocument.Parse(xml, LoadOptions.None);
        var result = new List<FeedItem>();
        foreach (var item in doc.Descendants("item").Take(limit))
        {
            var title = ((string?)item.Element("title"))?.Trim();
            var hash = ((string?)item.Element(Ns + "infoHash"))?.Trim();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(hash)) continue;
            var link = ((string?)item.Element("link"))?.Trim();
            var published = DateTimeOffset.TryParse((string?)item.Element("pubDate"), CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var parsedDate)
                ? parsedDate.UtcDateTime : (DateTime?)null;
            var seeders = IndexerParsing.ParseInt((string?)item.Element(Ns + "seeders"));
            var leechers = IndexerParsing.ParseInt((string?)item.Element(Ns + "leechers"));
            var size = IndexerParsing.ParseSize((string?)item.Element(Ns + "size"));
            var magnet = IndexerParsing.BuildMagnet(hash, title, Trackers);

            result.Add(new FeedItem(
                hash.ToUpperInvariant(),
                new ReleaseCandidate
                {
                    ReleaseName = title,
                    Magnet = magnet,
                    InfoHash = hash,
                    Seeders = seeders,
                    Leechers = leechers,
                    SizeBytes = size,
                    IndexerId = indexer.Id,
                    Source = indexer.Name,
                    PublishDate = published
                },
                new CatalogItemDto
                {
                    ExternalId = hash.ToUpperInvariant(),
                    ReleaseName = title,
                    SourceUrl = link,
                    InfoHash = hash,
                    MagnetUri = magnet,
                    Seeders = seeders,
                    Leechers = leechers,
                    SizeBytes = size > 0 ? size : null,
                    PublishedAt = published,
                    MediaType = MediaType.Anime
                }));
        }
        return result;
    }

    internal sealed record FeedItem(string ExternalId, ReleaseCandidate Candidate, CatalogItemDto CatalogItem);
}
