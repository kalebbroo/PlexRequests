using System.Text.RegularExpressions;

namespace PlexRequests.Downloader.Ranking;

public interface IReleaseParser
{
    ParsedRelease Parse(string releaseName);
    /// <summary>Best-effort resolution in pixels-high from a provider quality label like "1080p".</summary>
    int ResolutionFromLabel(string? label);
}

public partial class ReleaseParser : IReleaseParser
{
    public ParsedRelease Parse(string releaseName)
    {
        var name = releaseName ?? string.Empty;

        int resolution =
            Rx(name, @"\b(2160p|4k|uhd)\b") ? 2160 :
            Rx(name, @"\b1080p\b") ? 1080 :
            Rx(name, @"\b720p\b") ? 720 :
            Rx(name, @"\b(480p|576p|sd)\b") ? 480 : 0;

        ReleaseSource source =
            Rx(name, @"\bremux\b") ? ReleaseSource.Remux :
            Rx(name, @"\b(bluray|blu-ray|bdrip|brrip)\b") ? ReleaseSource.BluRay :
            Rx(name, @"\b(web-?dl|webdl|amzn|nf|dsnp|hmax)\b") ? ReleaseSource.WebDl :
            Rx(name, @"\bweb-?rip\b") ? ReleaseSource.WebRip :
            Rx(name, @"\b(hdtv|pdtv)\b") ? ReleaseSource.Hdtv :
            Rx(name, @"\b(cam|ts|telesync|hdcam)\b") ? ReleaseSource.Cam :
            ReleaseSource.Unknown;

        string? codec =
            Rx(name, @"\b(x265|h\.?265|hevc)\b") ? "x265" :
            Rx(name, @"\b(x264|h\.?264|avc)\b") ? "x264" :
            Rx(name, @"\bav1\b") ? "av1" : null;

        bool hdr = Rx(name, @"\b(hdr|hdr10|dv|dolby\.?vision)\b");
        bool proper = Rx(name, @"\b(proper|repack)\b");

        // Group: trailing "-GROUP" token.
        string? group = null;
        var m = GroupRegex().Match(name);
        if (m.Success) group = m.Groups[1].Value;

        return new ParsedRelease
        {
            Resolution = resolution,
            Source = source,
            Codec = codec,
            Hdr = hdr,
            ProperOrRepack = proper,
            Group = group
        };
    }

    public int ResolutionFromLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return 0;
        var l = label.ToLowerInvariant();
        if (l.Contains("2160") || l.Contains("4k")) return 2160;
        if (l.Contains("1080")) return 1080;
        if (l.Contains("720")) return 720;
        if (l.Contains("480") || l.Contains("576")) return 480;
        return 0;
    }

    private static bool Rx(string input, string pattern) =>
        Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"-([A-Za-z0-9]+)(?:\.[A-Za-z0-9]+)?$")]
    private static partial Regex GroupRegex();
}
