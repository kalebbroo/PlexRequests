using System.Xml.Linq;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// Generic Torznab provider (Jackett / Prowlarr). Torznab is the aggregator ecosystem's standard search
/// API: point one or more configured endpoints here (<see cref="IndexerOptions.Torznab"/>) and every
/// tracker set up in the aggregator becomes searchable with no site-specific scraping code — the practical
/// fix for content the built-in public sources index poorly (kids'/preschool shows especially, which EZTV
/// rarely carries and which need indexers with real TV/Kids category coverage). Queries use the proper
/// capability per media type (t=movie / t=tvsearch / t=music) with standard categories (2000 movies,
/// 3000 music, 5000 TV, 5070 TV/Anime), plus a generic music fallback for endpoints that advertise music
/// categories but not the optional music-search function.
/// Only results that yield a magnet (magneturl attr, magnet link, or an info hash to build one from)
/// are returned — the download client is magnet-only.
/// </summary>
public class TorznabIndexerProvider(HttpClient http, IIndexerFetch fetch, ILogger<TorznabIndexerProvider> logger)
    : IIndexerImplementation, IReleaseFeedSource
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
    private readonly IIndexerFetch _fetch = fetch;
    private readonly ILogger<TorznabIndexerProvider> _logger = logger;

    public string Key => "Torznab";

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(IndexerConfigDto indexer, FulfillmentJobDto job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(indexer.Url) || string.IsNullOrWhiteSpace(job.Title))
            return Array.Empty<ReleaseCandidate>();

        // One configured endpoint = one call here. This used to loop over every endpoint in the downloader's
        // appsettings, which is why they all shared a single identity, enable flag and health record.
        var byKey = new Dictionary<string, ReleaseCandidate>(StringComparer.OrdinalIgnoreCase);
        InvalidDataException? musicQueryError = null;
        var completedQuery = false;
        foreach (var url in BuildQueryUrls(indexer, job))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var xml = await _fetch.GetStringAsync(_http, indexer, url, ct);
                foreach (var c in ParseFeed(xml, indexer))
                    byKey.TryAdd(c.Acquisition.SourceId ?? c.Acquisition.Locator, c);
                completedQuery = true;
            }
            catch (InvalidDataException ex) when (job.MediaType == MediaType.Music)
            {
                // Some Torznab implementations return an XML error for t=music despite exposing 3000
                // categories. The following t=search query is the standards-compatible fallback.
                musicQueryError = ex;
                continue;
            }
        }
        if (!completedQuery && musicQueryError is not null) throw musicQueryError;
        return byKey.Values.ToList();
    }

    public async Task<ReleaseFeedPage> FetchAsync(
        IndexerConfigDto indexer,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(indexer.Url))
            throw new InvalidOperationException("The Torznab API URL is not configured.");

        var items = new List<CatalogItemDto>();
        const int pageSize = 100;
        const int maxPages = 10;
        for (var page = 0; page < maxPages; page++)
        {
            var separator = indexer.Url.Contains('?') ? "&" : "?";
            var url = $"{indexer.Url.TrimEnd('/')}{separator}apikey={Uri.EscapeDataString(indexer.ApiKey ?? string.Empty)}" +
                      $"&t=search&limit={pageSize}&offset={page * pageSize}";
            var xml = await _fetch.GetStringAsync(_http, indexer, url, cancellationToken);
            var candidates = ParseFeed(xml, indexer, out var rawItemCount);
            foreach (var candidate in candidates)
            {
                var hash = MagnetUtil.Normalize(candidate.Acquisition.SourceId)
                           ?? MagnetUtil.InfoHashFromMagnet(candidate.Acquisition.Locator);
                if (hash is null) continue;
                items.Add(new CatalogItemDto
                {
                    ExternalId = hash,
                    ReleaseName = candidate.ReleaseName,
                    InfoHash = hash,
                    MagnetUri = candidate.Acquisition.Locator,
                    ImdbId = candidate.ImdbId,
                    Seeders = candidate.SeedersKnown ? candidate.Seeders : null,
                    Leechers = candidate.SeedersKnown ? candidate.Leechers : null,
                    SizeBytes = candidate.SizeKnown ? candidate.SizeBytes : null,
                    PublishedAt = candidate.PublishDate,
                    Category = candidate.CategoryIds.Count == 0 ? null : string.Join(',', candidate.CategoryIds),
                    MediaType = Classify(candidate.CategoryIds)
                });
            }
            // A Torznab page may contain .torrent-only rows that this magnet-only client intentionally
            // discards. Pagination is determined by the server's raw row count, not the usable subset.
            if (string.IsNullOrWhiteSpace(cursor) || rawItemCount < pageSize
                || items.Any(x => string.Equals(x.ExternalId, cursor, StringComparison.OrdinalIgnoreCase)))
                break;
        }
        return ReleaseFeedCursor.Select(items, cursor, limit);
    }

    internal static IEnumerable<string> BuildQueryUrls(IndexerConfigDto indexer, FulfillmentJobDto job)
    {
        var endpointUrl = indexer.Url!;
        var sep = endpointUrl.Contains('?') ? "&" : "?";
        // Categories come from the row now. They were hardcoded to the Torznab standards, which are simply
        // wrong for trackers that use their own numbering — those returned nothing and looked "empty".
        var cat = indexer.CategoriesFor(job.MediaType);
        string Base(string t, string c) =>
            $"{endpointUrl.TrimEnd('/')}{sep}apikey={Uri.EscapeDataString(indexer.ApiKey ?? string.Empty)}&t={t}&cat={c}";

        if (job.MediaType == MediaType.Movie)
        {
            // An id search beats fuzzy text wherever the indexer supports it; the text query still runs
            // as a second net (imdbid support varies per tracker and the aggregator merges either way).
            var imdbDigits = DigitsOf(job.ImdbId);
            if (imdbDigits is not null) yield return Base("movie", cat) + $"&imdbid={imdbDigits}";
            var q = Base("movie", cat) + $"&q={Uri.EscapeDataString(job.Title)}";
            if (job.Year is int y) q += $"&year={y}";
            yield return q;
            yield break;
        }

        if (job.MediaType == MediaType.Music)
        {
            var searchText = job.Music?.BuildSearchText(job.Title) ?? job.Title;
            var specialized = Base("music", cat);
            if (!string.IsNullOrWhiteSpace(job.Music?.Artist))
                specialized += $"&artist={Uri.EscapeDataString(job.Music.Artist)}";
            if (job.RequestScope == RequestScopeKind.Album && !string.IsNullOrWhiteSpace(job.Music?.Album))
                specialized += $"&album={Uri.EscapeDataString(job.Music.Album)}";
            else if (job.RequestScope == RequestScopeKind.Track && !string.IsNullOrWhiteSpace(job.Music?.Track))
                specialized += $"&track={Uri.EscapeDataString(job.Music.Track)}";
            else
                specialized += $"&q={Uri.EscapeDataString(searchText)}";
            if (job.Year is int musicYear) specialized += $"&year={musicYear}";
            yield return specialized;
            yield return Base("search", cat) + $"&q={Uri.EscapeDataString(searchText)}";
            yield break;
        }

        var text = $"&q={Uri.EscapeDataString(job.Title)}";
        yield return Base("tvsearch", cat) + text; // unscoped — catches complete-series/multi-season packs
        var seasons = job.RequestedSeasons
            .Concat(job.SeasonTargets.Select(t => t.Season))
            .Concat(job.RequestedEpisodes.Select(e => e.Season))
            .Distinct().OrderBy(s => s).Take(4);
        foreach (var s in seasons)
            yield return Base("tvsearch", cat) + text + $"&season={s}";
    }

    private static List<ReleaseCandidate> ParseFeed(string xml, IndexerConfigDto indexer) =>
        ParseFeed(xml, indexer, out _);

    private static List<ReleaseCandidate> ParseFeed(
        string xml,
        IndexerConfigDto indexer,
        out int rawItemCount)
    {
        var candidates = new List<ReleaseCandidate>();
        var doc = XDocument.Parse(xml);
        var error = doc.Descendants("error").FirstOrDefault();
        if (error is not null)
        {
            var description = (string?)error.Attribute("description") ?? "The Torznab endpoint returned an error.";
            throw new InvalidDataException(description);
        }
        var items = doc.Descendants("item").ToList();
        rawItemCount = items.Count;
        foreach (var item in items)
        {
            var title = ((string?)item.Element("title"))?.Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;

            string? Attr(string name) => item.Elements(TorznabNs + "attr")
                .FirstOrDefault(a => string.Equals((string?)a.Attribute("name"), name, StringComparison.OrdinalIgnoreCase))
                ?.Attribute("value")?.Value;
            List<string> Attrs(string name) => item.Elements(TorznabNs + "attr")
                .Where(a => string.Equals((string?)a.Attribute("name"), name, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Attribute("value")?.Value)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();

            var infoHash = Attr("infohash");
            var link = (string?)item.Element("link");
            var magnet = Attr("magneturl");
            if (string.IsNullOrWhiteSpace(magnet) && link?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true)
                magnet = link;
            if (string.IsNullOrWhiteSpace(magnet) && !string.IsNullOrWhiteSpace(infoHash))
                magnet = IndexerParsing.BuildMagnet(infoHash!, title!, Trackers);
            if (string.IsNullOrWhiteSpace(magnet)) continue; // .torrent-file-only result; client is magnet-only

            var sizeRaw = Attr("size") ?? (string?)item.Element("size");
            bool sizeKnown = long.TryParse(sizeRaw, out var sz);
            long size = sizeKnown ? sz : 0;
            var seedersRaw = Attr("seeders");
            DateTime? published = DateTime.TryParse((string?)item.Element("pubDate"), out var pd) ? pd.ToUniversalTime() : null;
            int? season = int.TryParse(Attr("season"), out var sn) && sn > 0 ? sn : null;
            int? episode = int.TryParse(Attr("episode"), out var ep) && ep > 0 ? ep : null;
            var categories = Attrs("category")
                .SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(x => int.TryParse(x, out var id) ? id : (int?)null)
                .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();

            candidates.Add(new ReleaseCandidate
            {
                ReleaseName = title!,
                Acquisition = AcquisitionResource.Torrent(magnet!, infoHash),
                ImdbId = Attr("imdbid") ?? Attr("imdb"),
                Seeders = IndexerParsing.ParseInt(seedersRaw),
                Leechers = IndexerParsing.ParseInt(Attr("peers") ?? Attr("leechers")),
                SizeBytes = size,
                SeedersKnown = !string.IsNullOrWhiteSpace(seedersRaw),
                SizeKnown = sizeKnown,
                IndexerId = indexer.Id,
                Source = indexer.Name,
                PublishDate = published,
                Season = season,
                Episode = episode,
                CategoryIds = categories
            });
        }
        return candidates;
    }

    private static string? DigitsOf(string? imdbId)
    {
        if (string.IsNullOrWhiteSpace(imdbId)) return null;
        var digits = new string(imdbId.Where(char.IsDigit).ToArray()).TrimStart('0');
        return digits.Length == 0 ? null : digits;
    }

    private static MediaType? Classify(IReadOnlyList<int> categories)
    {
        if (categories.Any(x => x == 5070)) return MediaType.Anime;
        if (categories.Any(x => x is >= 3000 and < 4000)) return MediaType.Music;
        if (categories.Any(x => x is >= 5000 and < 6000)) return MediaType.TvShow;
        if (categories.Any(x => x is >= 2000 and < 3000)) return MediaType.Movie;
        return null;
    }

    /// <summary>
    /// The real thing: <c>t=caps</c> returns the endpoint's own category tree and the search params it
    /// supports. That tree is the answer to the most common way an indexer is misconfigured — a tracker
    /// numbering its categories privately returns nothing for the Torznab standards, and "nothing" reads
    /// exactly like "no matches".
    /// </summary>
    public async Task<IndexerCapabilitiesDto> TestAsync(IndexerConfigDto indexer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(indexer.Url))
            return new IndexerCapabilitiesDto { Reachable = false, Message = "No API URL is configured for this indexer." };

        var sep = indexer.Url.Contains('?') ? "&" : "?";
        var url = $"{indexer.Url.TrimEnd('/')}{sep}apikey={Uri.EscapeDataString(indexer.ApiKey ?? string.Empty)}&t=caps";

        var started = System.Diagnostics.Stopwatch.StartNew();
        string xml;
        try
        {
            xml = await _fetch.GetStringAsync(_http, indexer, url, ct);
            started.Stop();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            started.Stop();
            _logger.LogWarning(ex, "Torznab caps probe failed for \"{Endpoint}\"", indexer.Name);
            return new IndexerCapabilitiesDto { Reachable = false, Message = ex.Message };
        }

        var caps = new IndexerCapabilitiesDto { Reachable = true, ResponseMs = (int)started.ElapsedMilliseconds };
        try
        {
            var doc = XDocument.Parse(xml);

            // Jackett answers an auth failure with a 200 and an <error> body, so a parse alone isn't proof.
            var error = doc.Descendants("error").FirstOrDefault();
            if (error is not null)
            {
                caps.Reachable = false;
                caps.Message = (string?)error.Attribute("description") ?? "The endpoint returned an error.";
                return caps;
            }

            foreach (var s in doc.Descendants("searching").Elements())
            {
                var available = string.Equals((string?)s.Attribute("available"), "yes", StringComparison.OrdinalIgnoreCase);
                if (!available) continue;
                if (s.Name.LocalName is "movie-search") caps.SupportsMovieSearch = true;
                if (s.Name.LocalName is "tv-search") caps.SupportsTvSearch = true;
                if (s.Name.LocalName is "music-search") caps.SupportsMusicSearch = true;
                var supported = (string?)s.Attribute("supportedParams");
                if (!string.IsNullOrWhiteSpace(supported))
                    caps.SupportedParams.AddRange(supported.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            caps.SupportedParams = caps.SupportedParams.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var cat in doc.Descendants("categories").Elements("category"))
            {
                if (!int.TryParse((string?)cat.Attribute("id"), out var id)) continue;
                caps.Categories.Add(new IndexerCategoryDto { Id = id, Name = (string?)cat.Attribute("name") ?? id.ToString() });
                foreach (var sub in cat.Elements("subcat"))
                    if (int.TryParse((string?)sub.Attribute("id"), out var subId))
                        caps.Categories.Add(new IndexerCategoryDto
                        {
                            Id = subId, ParentId = id, Name = (string?)sub.Attribute("name") ?? subId.ToString()
                        });
            }

            caps.Message = caps.Categories.Count > 0
                ? $"Connected in {caps.ResponseMs} ms — {caps.Categories.Count} categories available."
                : $"Connected in {caps.ResponseMs} ms, but the endpoint advertised no categories.";
        }
        catch (Exception ex)
        {
            caps.Reachable = false;
            caps.Message = $"The endpoint answered but the response wasn't a Torznab caps document ({ex.Message}).";
        }
        return caps;
    }
}
