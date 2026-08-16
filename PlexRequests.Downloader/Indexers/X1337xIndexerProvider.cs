using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;
using HtmlAgilityPack;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Ranking;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// 1337x provider (movies + TV). 1337x has no API, so this parses HTML loaded by the persistent browser:
/// the category-search page lists rows (name/seeds/leeches/size), while magnets live on detail pages.
/// Browser transport is isolated behind an interface because layout changes remain scraper-specific and
/// no other provider should inherit Chrome's cost or lifecycle.
/// </summary>
public partial class X1337xIndexerProvider(HttpClient http, IReleaseParser parser, IInteractiveBrowserTransport browser, ILogger<X1337xIndexerProvider> logger)
    : IIndexerImplementation
{
    private readonly HttpClient _http = http;
    private readonly IReleaseParser _parser = parser;
    private readonly IInteractiveBrowserTransport _browser = browser;
    private readonly ILogger<X1337xIndexerProvider> _logger = logger;

    public string Key => "1337x";

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(IndexerConfigDto indexer, FulfillmentJobDto job, CancellationToken ct)
    {
        var category = job.MediaType switch
        {
            MediaType.Movie => "Movies",
            MediaType.Anime => "Anime",
            _ => "TV"
        };
        var terms = job.MediaType == MediaType.Movie && job.Year is int y ? $"{job.Title} {y}" : job.Title;

        // Each search only sees page 1, so for a season-scoped TV job a title-only query can miss the
        // wanted season entirely (a busy show's newest uploads push older seasons' packs off the page).
        // Add a "title season N" query per requested season (bounded) so those packs surface too.
        var termsList = new List<string> { terms };
        if (job.MediaType != MediaType.Movie)
        {
            var seasons = job.RequestedSeasons
                .Concat(job.SeasonTargets.Select(t => t.Season))
                .Concat(job.RequestedEpisodes.Select(e => e.Season))
                .Distinct().OrderBy(s => s).Take(4);
            termsList.AddRange(seasons.Select(s => $"{job.Title} season {s}"));
        }

        var rowsByPath = new Dictionary<string, RowInfo>();
        foreach (var t in termsList)
        {
            var query = BuildQuery(t);
            if (string.IsNullOrWhiteSpace(query)) continue;

            // Deliberately NOT wrapped in a swallow-and-continue. A refusal used to be logged as a warning
            // and reported upward as "search succeeded, zero results", so a permanently blocked indexer was
            // indistinguishable from a quiet one and stayed green in the admin panel for weeks. Let the
            // block propagate; IndexerClient records it as a failure with a reason.
            var html = await BrowserGetStringAsync($"/category-search/{query}/{category}/1/", ct);

            if (LooksLikeChallenge(html))
                throw new IndexerBlockedException(IndexerBlockReason.CloudflareChallenge,
                    "1337x served an anti-bot interstitial instead of results");

            foreach (var row in ParseRows(html)) rowsByPath.TryAdd(row.DetailPath, row);
        }

        if (rowsByPath.Count == 0) return Array.Empty<ReleaseCandidate>();

        // Magnets only exist on each torrent's detail page, so we can only open a bounded number of rows.
        // Which rows we pick is the whole ballgame: this used to take the top 8 purely by seeder count,
        // BEFORE considering quality at all. On this site the highest-seeded TV rows are routinely the
        // smaller 720p ones, so every 1080p release on the page was discarded before the ranker ever saw
        // it — and the job then settled for 720p. Filtering by the job's quality floor first costs nothing
        // (the row already carries the release name) and is the direct fix.
        var cap = Math.Clamp(indexer.MaxDetailFetches, 1, 100);
        var candidateRows = SelectRows(rowsByPath.Values, job, cap);

        var candidates = new List<ReleaseCandidate>();
        var results = new List<ReleaseCandidate?>();
        var gate = new object();
        await Parallel.ForEachAsync(candidateRows,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelDetailFetches, CancellationToken = ct },
            async (row, token) =>
            {
                var c = await ResolveMagnetAsync(row, indexer, token);
                lock (gate) results.Add(c);
            });
        foreach (var c in results) if (c is not null) candidates.Add(c);

        return candidates;
    }

    // Opening a detail page per row is the expensive part, so keep the concurrency against one host modest.
    private const int MaxParallelDetailFetches = 4;

    /// <summary>
    /// Choose which listing rows are worth opening. Rows whose parsed resolution already meets the job's
    /// quality floor come first (best-seeded first), and only if there aren't enough of those do we fall
    /// back to the rest — so a page full of well-seeded 720p can no longer crowd out the 1080p releases,
    /// while a title that genuinely only exists below the floor still yields candidates.
    /// </summary>
    private List<RowInfo> SelectRows(IEnumerable<RowInfo> rows, FulfillmentJobDto job, int cap)
    {
        int floor = (int)job.Quality;
        var ordered = rows.OrderByDescending(r => r.Seeders).ToList();
        if (floor <= 0) return ordered.Take(cap).ToList();

        var atFloor = ordered.Where(r => _parser.Parse(r.Name).Resolution >= floor).ToList();
        var below = ordered.Except(atFloor).ToList();

        var picked = atFloor.Take(cap).ToList();
        if (picked.Count < cap) picked.AddRange(below.Take(cap - picked.Count));

        if (atFloor.Count > 0)
            _logger.LogDebug("1337x: {AtFloor} of {Total} row(s) meet the {Floor}p floor; opening {Opened}",
                atFloor.Count, ordered.Count, floor, picked.Count);
        return picked;
    }

    private async Task<ReleaseCandidate?> ResolveMagnetAsync(RowInfo row, IndexerConfigDto indexer, CancellationToken ct)
    {
        try
        {
            var detailHtml = await BrowserGetStringAsync(row.DetailPath, ct);
            var doc = new HtmlDocument();
            doc.LoadHtml(detailHtml);
            var magnet = doc.DocumentNode.SelectSingleNode("//a[starts-with(@href,'magnet:')]")?.GetAttributeValue("href", null);
            if (string.IsNullOrWhiteSpace(magnet)) return null;
            magnet = HttpUtility.HtmlDecode(magnet);

            return new ReleaseCandidate
            {
                ReleaseName = row.Name,
                Magnet = magnet,
                InfoHash = InfoHashRegex().Match(magnet) is { Success: true } m ? m.Groups[1].Value : null,
                Seeders = row.Seeders,
                Leechers = row.Leechers,
                SizeBytes = row.SizeBytes,
                // These come from scraping the listing row and often fail to parse. Saying so lets the
                // ranker treat "unknown" differently from a genuine zero instead of silently rejecting it.
                SeedersKnown = row.Seeders > 0,
                SizeKnown = row.SizeBytes > 0,
                IndexerId = indexer.Id,
                Source = indexer.Name
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "1337x detail fetch failed for {Path}", row.DetailPath);
            return null;
        }
    }

    /// <summary>
    /// The newest uploads in a category, for the recommended feed. Deliberately does NOT open detail pages:
    /// a poster row needs a title, not a magnet, so this costs one request per media type instead of dozens.
    /// </summary>
    public async Task<IReadOnlyList<ReleaseCandidate>> BrowseLatestAsync(IndexerConfigDto indexer, MediaType mediaType, int limit, CancellationToken ct)
    {
        var category = mediaType switch
        {
            MediaType.Movie => "Movies",
            MediaType.Anime => "Anime",
            _ => "TV"
        };

        string html;
        try
        {
            // The category listing is already newest-first, which is exactly what "latest" wants.
            html = await BrowserGetStringAsync($"/cat/{category}/1/", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "1337x browse failed for {Category}", category);
            return Array.Empty<ReleaseCandidate>();
        }

        if (LooksLikeChallenge(html))
        {
            _logger.LogWarning("1337x returned a Cloudflare/anti-bot page while browsing {Category}", category);
            return Array.Empty<ReleaseCandidate>();
        }

        return ParseRows(html).Take(Math.Clamp(limit, 1, 100)).Select(r => new ReleaseCandidate
        {
            ReleaseName = r.Name,
            // No detail page was opened, so there's no magnet. Nothing downstream of the feed needs one.
            Magnet = string.Empty,
            Seeders = r.Seeders,
            Leechers = r.Leechers,
            SizeBytes = r.SizeBytes,
            SeedersKnown = r.Seeders > 0,
            SizeKnown = r.SizeBytes > 0,
            IndexerId = indexer.Id,
            Source = indexer.Name
        }).ToList();
    }

    private List<RowInfo> ParseRows(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var rowNodes = doc.DocumentNode.SelectNodes("//table[contains(@class,'table-list')]/tbody/tr");
        var rows = new List<RowInfo>();
        if (rowNodes is null) return rows;

        foreach (var tr in rowNodes)
        {
            // The name cell has an icon link then the torrent link.
            var link = tr.SelectSingleNode(".//td[contains(@class,'name')]/a[starts-with(@href,'/torrent/')]")
                       ?? tr.SelectSingleNode(".//td[contains(@class,'name')]/a[2]");
            var href = link?.GetAttributeValue("href", null);
            if (string.IsNullOrWhiteSpace(href)) continue;

            var name = HttpUtility.HtmlDecode(link!.InnerText).Trim();
            var seeds = ParseInt(tr.SelectSingleNode(".//td[contains(@class,'seeds')]"));
            var leech = ParseInt(tr.SelectSingleNode(".//td[contains(@class,'leeches')]"));

            // The size cell holds "1.4 GB" plus a mobile <span> duplicate of seeds — take the leading text node.
            var sizeTd = tr.SelectSingleNode(".//td[contains(@class,'size')]");
            var sizeText = sizeTd?.ChildNodes.FirstOrDefault(n => n.NodeType == HtmlNodeType.Text)?.InnerText
                           ?? sizeTd?.InnerText ?? string.Empty;

            rows.Add(new RowInfo(name, href, seeds, leech, ParseSize(sizeText)));
        }
        return rows;
    }

    private static string BuildQuery(string terms)
    {
        var cleaned = Regex.Replace(terms, @"[^\w\s]", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return Uri.EscapeDataString(cleaned).Replace("%20", "+");
    }

    private static bool LooksLikeChallenge(string html) =>
        html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("Checking your browser", StringComparison.OrdinalIgnoreCase);

    private Task<string> BrowserGetStringAsync(string path, CancellationToken ct)
    {
        var target = _http.BaseAddress is null ? path : new Uri(_http.BaseAddress, path).ToString();
        return _browser.GetStringAsync(target, ct);
    }

    // Size/count parsing lives in IndexerParsing — this file used to carry byte-identical private copies.
    private static int ParseInt(HtmlNode? node) => node is null ? 0 : IndexerParsing.ParseInt(node.InnerText);

    private static long ParseSize(string s) => IndexerParsing.ParseSize(s);

    [GeneratedRegex(@"urn:btih:([A-Za-z0-9]+)")]
    private static partial Regex InfoHashRegex();

    private sealed record RowInfo(string Name, string DetailPath, int Seeders, int Leechers, long SizeBytes);
}
