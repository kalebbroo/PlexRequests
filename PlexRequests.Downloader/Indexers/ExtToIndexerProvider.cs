using System.Text.RegularExpressions;
using System.Web;
using HtmlAgilityPack;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// ext.to (general — movies + TV). No API, so this scrapes: the search page links to torrent detail
/// pages, and each detail page carries the magnet plus labelled Seeders/Size. Labelled-regex
/// extraction is used (rather than fixed column positions) so it tolerates layout differences.
///
/// NOTE: ext.to is typically behind Cloudflare, which blocks plain HTTP clients from datacenter IPs
/// (detected here → returns nothing). The search-path template and base URL are configurable so the
/// selectors/URL can be tuned against a live page without code changes.
/// </summary>
public partial class ExtToIndexerProvider(HttpClient http, IOptions<IndexerOptions> options, IIndexerFetch fetch, ILogger<ExtToIndexerProvider> logger)
    : IIndexerImplementation
{
    private readonly HttpClient _http = http;
    private readonly IndexerOptions _opts = options.Value;
    private readonly IIndexerFetch _fetch = fetch;
    private readonly ILogger<ExtToIndexerProvider> _logger = logger;

    public string Key => "ext.to";

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(IndexerConfigDto indexer, FulfillmentJobDto job, CancellationToken ct)
    {
        if (!_opts.ExtToEnabled || string.IsNullOrWhiteSpace(job.Title)) return Array.Empty<ReleaseCandidate>();

        var terms = AcquisitionQuery.Build(job);
        var query = Uri.EscapeDataString(Regex.Replace(terms, @"\s+", " ").Trim());
        var searchUrl = _opts.ExtToSearchPath.Replace("{query}", query);

        // Through the shared seam, so a refusal is reported as a block rather than as an empty result.
        // ext.to was left behind when 1337x was converted and kept showing a clean health record while
        // contributing nothing across 16 searches — the exact condition that change existed to end.
        var html = await _fetch.GetStringAsync(_http, indexer, searchUrl, ct);

        if (LooksLikeChallenge(html))
            throw new IndexerBlockedException(IndexerBlockReason.CloudflareChallenge,
                "ext.to served an anti-bot interstitial instead of results");

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Strategy 1: magnets present directly on the search results page.
        var inline = ParseInlineMagnets(doc, indexer);
        if (inline.Count > 0) return inline;

        // Strategy 2: follow the top detail pages and pull magnet + labelled seeders/size from each.
        var detailPaths = doc.DocumentNode.SelectNodes("//a[contains(@href,'/torrent/')]")?
            .Select(a => a.GetAttributeValue("href", null))
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct()
            .Take(Math.Clamp(indexer.MaxDetailFetches, 1, 100))
            .ToList() ?? new();

        if (detailPaths.Count == 0) return Array.Empty<ReleaseCandidate>();

        var results = await Task.WhenAll(detailPaths.Select(p => FromDetailAsync(p!, indexer, ct)));
        return results.Where(c => c is not null).Select(c => c!).ToList();
    }

    private List<ReleaseCandidate> ParseInlineMagnets(HtmlDocument doc, IndexerConfigDto indexer)
    {
        var magnets = doc.DocumentNode.SelectNodes("//a[starts-with(@href,'magnet:')]");
        var list = new List<ReleaseCandidate>();
        if (magnets is null) return list;

        foreach (var a in magnets)
        {
            var magnet = HttpUtility.HtmlDecode(a.GetAttributeValue("href", ""));
            if (string.IsNullOrWhiteSpace(magnet)) continue;

            // Row context = nearest ancestor tr/li/div for seeders/size heuristics.
            var row = a.Ancestors().FirstOrDefault(n => n.Name is "tr" or "li" or "div");
            var rowText = row is null ? string.Empty : VisibleText(row);
            list.Add(new ReleaseCandidate
            {
                ReleaseName = NameFromMagnet(magnet),
                Magnet = magnet,
                InfoHash = InfoHashRegex().Match(magnet) is { Success: true } m ? m.Groups[1].Value : null,
                Seeders = Labelled(rowText, "seed"),
                Leechers = Labelled(rowText, "leech"),
                SizeBytes = IndexerParsing.ParseSize(SizeNear(rowText)),
                IndexerId = indexer.Id,
                Source = indexer.Name,
                SeedersKnown = Labelled(rowText, "seed") > 0,
                SizeKnown = IndexerParsing.ParseSize(SizeNear(rowText)) > 0
            });
        }
        return list;
    }

    private async Task<ReleaseCandidate?> FromDetailAsync(string path, IndexerConfigDto indexer, CancellationToken ct)
    {
        try
        {
            var html = await _http.GetStringAsync(path, ct);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var magnet = doc.DocumentNode.SelectSingleNode("//a[starts-with(@href,'magnet:')]")?.GetAttributeValue("href", null);
            if (string.IsNullOrWhiteSpace(magnet)) return null;
            magnet = HttpUtility.HtmlDecode(magnet);

            var name = doc.DocumentNode.SelectSingleNode("//h1")?.InnerText?.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = NameFromMagnet(magnet);

            var text = VisibleText(doc.DocumentNode);
            return new ReleaseCandidate
            {
                ReleaseName = HttpUtility.HtmlDecode(name!).Trim(),
                Magnet = magnet,
                InfoHash = InfoHashRegex().Match(magnet) is { Success: true } m ? m.Groups[1].Value : null,
                Seeders = Labelled(text, "seed"),
                Leechers = Labelled(text, "leech"),
                SizeBytes = IndexerParsing.ParseSize(SizeNear(text)),
                IndexerId = indexer.Id,
                Source = indexer.Name,
                SeedersKnown = Labelled(text, "seed") > 0,
                SizeKnown = IndexerParsing.ParseSize(SizeNear(text)) > 0
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "ext.to detail fetch failed for {Path}", path);
            return null;
        }
    }

    /// <summary>Join text nodes with spaces so adjacent table cells (e.g. "88" + "2.0 GB") don't merge.</summary>
    private static string VisibleText(HtmlNode node) =>
        string.Join(" ", node.DescendantsAndSelf()
            .Where(n => n.NodeType == HtmlNodeType.Text)
            .Select(n => HttpUtility.HtmlDecode(n.InnerText).Trim())
            .Where(s => s.Length > 0));

    private static string NameFromMagnet(string magnet)
    {
        var m = Regex.Match(magnet, @"[?&]dn=([^&]+)");
        return m.Success ? HttpUtility.UrlDecode(m.Groups[1].Value) : "unknown";
    }

    /// <summary>Find a number that follows a label like "seeders" / "leechers".</summary>
    private static int Labelled(string text, string label)
    {
        var m = Regex.Match(text, label + @"[a-z]*\s*[:\-]?\s*(\d[\d,]*)", RegexOptions.IgnoreCase);
        return m.Success ? IndexerParsing.ParseInt(m.Groups[1].Value) : 0;
    }

    private static string SizeNear(string text)
    {
        var m = Regex.Match(text, @"size\s*[:\-]?\s*([\d.,]+\s*(?:TB|GB|MB|GiB|MiB|KiB))", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        // Fallback: first size-looking token anywhere.
        var any = Regex.Match(text, @"[\d.,]+\s*(?:TB|GB|MB|GiB|MiB)", RegexOptions.IgnoreCase);
        return any.Success ? any.Value : string.Empty;
    }

    private static bool LooksLikeChallenge(string html) =>
        html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("Checking your browser", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"urn:btih:([A-Za-z0-9]+)")]
    private static partial Regex InfoHashRegex();
}
