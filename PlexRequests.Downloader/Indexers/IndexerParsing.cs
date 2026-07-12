using System.Globalization;
using System.Text.RegularExpressions;

namespace PlexRequests.Downloader.Indexers;

/// <summary>Shared helpers for the scraping/RSS indexer providers.</summary>
public static partial class IndexerParsing
{
    /// <summary>Parse "1.4 GB" / "13.7 GiB" / "700 MB" → bytes (0 if unparseable).</summary>
    public static long ParseSize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var m = SizeRegex().Match(s);
        if (!m.Success) return 0;
        if (!double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
            return 0;
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

    /// <summary>Build a magnet URI from an info hash + display name + tracker list.</summary>
    public static string BuildMagnet(string infoHash, string name, IReadOnlyList<string> trackers)
    {
        var tr = string.Concat(trackers.Select(t => "&tr=" + Uri.EscapeDataString(t)));
        return $"magnet:?xt=urn:btih:{infoHash}&dn={Uri.EscapeDataString(name)}{tr}";
    }

    /// <summary>Digits only → int (0 on failure). Tolerates commas/labels.</summary>
    public static int ParseInt(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var digits = new string(s.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    [GeneratedRegex(@"([\d.,]+)\s*(TB|GB|MB|KB|B|TiB|GiB|MiB|KiB)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();
}
