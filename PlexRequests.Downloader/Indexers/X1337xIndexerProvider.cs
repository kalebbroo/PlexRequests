using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;
using HtmlAgilityPack;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// 1337x provider (movies + TV). 1337x has no API, so this scrapes the HTML: the category-search
/// page lists rows (name/seeds/leeches/size) but the magnet lives on each torrent's detail page, so
/// we open the top rows to fetch magnets. Two caveats vs the API providers: it's more fragile (page
/// layout changes break parsing) and 1337x is often behind Cloudflare, which can block a plain
/// HttpClient from a datacenter IP. Runs fine from a residential/VPN egress.
/// </summary>
public partial class X1337xIndexerProvider(HttpClient http, IOptions<IndexerOptions> options, ILogger<X1337xIndexerProvider> logger)
    : IIndexerProvider
{
    private readonly HttpClient _http = http;
    private readonly IndexerOptions _opts = options.Value;
    private readonly ILogger<X1337xIndexerProvider> _logger = logger;

    public string Name => "1337x";
    public bool Supports(MediaType mediaType) => mediaType is MediaType.Movie or MediaType.TvShow or MediaType.Anime;

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(FulfillmentJobDto job, CancellationToken ct)
    {
        if (!_opts.X1337xEnabled) return Array.Empty<ReleaseCandidate>();

        var category = job.MediaType switch
        {
            MediaType.Movie => "Movies",
            MediaType.Anime => "Anime",
            _ => "TV"
        };
        var terms = job.MediaType == MediaType.Movie && job.Year is int y ? $"{job.Title} {y}" : job.Title;
        var query = BuildQuery(terms);
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<ReleaseCandidate>();

        var searchUrl = $"/category-search/{query}/{category}/1/";
        string html;
        try
        {
            html = await _http.GetStringAsync(searchUrl, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "1337x search request failed for \"{Title}\"", job.Title);
            return Array.Empty<ReleaseCandidate>();
        }

        if (LooksLikeChallenge(html))
        {
            _logger.LogWarning("1337x returned a Cloudflare/anti-bot page; skipping (search for \"{Title}\")", job.Title);
            return Array.Empty<ReleaseCandidate>();
        }

        var rows = ParseRows(html);
        if (rows.Count == 0) return Array.Empty<ReleaseCandidate>();

        // Only open the strongest rows for magnets (each detail page is a separate request).
        var topRows = rows.OrderByDescending(r => r.Seeders).Take(Math.Clamp(_opts.X1337xMaxDetail, 1, 25)).ToList();
        var candidates = new List<ReleaseCandidate>();
        var results = await Task.WhenAll(topRows.Select(r => ResolveMagnetAsync(r, ct)));
        foreach (var c in results) if (c is not null) candidates.Add(c);

        return candidates;
    }

    private async Task<ReleaseCandidate?> ResolveMagnetAsync(RowInfo row, CancellationToken ct)
    {
        try
        {
            var detailHtml = await _http.GetStringAsync(row.DetailPath, ct);
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
                Source = Name
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "1337x detail fetch failed for {Path}", row.DetailPath);
            return null;
        }
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

    private static int ParseInt(HtmlNode? node)
    {
        if (node is null) return 0;
        var digits = new string(node.InnerText.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    private static long ParseSize(string s)
    {
        var m = Regex.Match(s, @"([\d.,]+)\s*(TB|GB|MB|KB|B|TiB|GiB|MiB|KiB)", RegexOptions.IgnoreCase);
        if (!m.Success) return 0;
        if (!double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var val)) return 0;
        double mult = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "TB" or "TIB" => 1024d * 1024 * 1024 * 1024,
            "GB" or "GIB" => 1024d * 1024 * 1024,
            "MB" or "MIB" => 1024d * 1024,
            "KB" or "KIB" => 1024d,
            _ => 1d
        };
        return (long)(val * mult);
    }

    [GeneratedRegex(@"urn:btih:([A-Za-z0-9]+)")]
    private static partial Regex InfoHashRegex();

    private sealed record RowInfo(string Name, string DetailPath, int Seeders, int Leechers, long SizeBytes);
}
