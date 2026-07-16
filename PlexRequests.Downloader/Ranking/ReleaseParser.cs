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

        var (season, seasonEnd, episode, isPack, looksLikeComplete) = ParseSeasonEpisode(name);

        int? year = null;
        var yearMatch = Regex.Match(name, @"\b(19\d{2}|20\d{2})\b", RxOpts);
        if (yearMatch.Success) year = int.Parse(yearMatch.Groups[1].Value);

        return new ParsedRelease
        {
            Title = ExtractTitle(name),
            Resolution = resolution,
            Source = source,
            Codec = codec,
            Hdr = hdr,
            ProperOrRepack = proper,
            Group = group,
            Season = season,
            SeasonEnd = seasonEnd,
            Episode = episode,
            IsSeasonPack = isPack,
            LooksLikeCompleteSeries = looksLikeComplete,
            Year = year
        };
    }

    /// <summary>
    /// Extract season/episode and whether the release is a whole-season/complete pack, from the name.
    /// A single episode (SxxExx or NxNN) sets Season+Episode; a pack (Sxx with no Exx, "Season N",
    /// "Complete", or a multi-season range Sxx-Sxx) sets IsSeasonPack (+Season/SeasonEnd when named).
    /// <paramref name="name"/> failing to parse ANY season at all is distinct from an explicit "complete"
    /// match — only the latter sets <c>looksLikeCompleteSeries</c>, so a release that simply didn't parse
    /// isn't later treated as matching every requested season.
    /// </summary>
    private static (int? season, int? seasonEnd, int? episode, bool isPack, bool looksLikeCompleteSeries) ParseSeasonEpisode(string name)
    {
        // Single episode: S01E02 / S1E2 / 1x02.
        var ep = Regex.Match(name, @"\bS(\d{1,2})[\s._-]*E(\d{1,3})\b", RxOpts);
        if (ep.Success)
            return (int.Parse(ep.Groups[1].Value), null, int.Parse(ep.Groups[2].Value), false, false);
        var alt = Regex.Match(name, @"\b(\d{1,2})x(\d{1,3})\b", RxOpts);
        if (alt.Success)
            return (int.Parse(alt.Groups[1].Value), null, int.Parse(alt.Groups[2].Value), false, false);

        // No explicit episode ⇒ look for pack signals.
        var multi = Regex.Match(name, @"\bS(\d{1,2})[\s._-]*-[\s._-]*S(\d{1,2})\b", RxOpts); // S01-S05
        if (multi.Success) return (int.Parse(multi.Groups[1].Value), int.Parse(multi.Groups[2].Value), null, true, false);

        var seasonWord = Regex.Match(name, @"\bseason[\s._-]*(\d{1,2})\b", RxOpts);          // Season 1
        if (seasonWord.Success) return (int.Parse(seasonWord.Groups[1].Value), null, null, true, false);

        var sOnly = Regex.Match(name, @"\bS(\d{1,2})\b(?![\s._-]*E\d)", RxOpts);             // S01 (no Exx)
        if (sOnly.Success) return (int.Parse(sOnly.Groups[1].Value), null, null, true, false);

        if (Rx(name, @"\b(complete|complete[\s._-]*series)\b")) return (null, null, null, true, true); // whole-series pack, explicit

        return (null, null, null, false, false);
    }

    // Structural markers that end the title portion of a release name. The title is whatever precedes the
    // earliest of these. Kept deliberately broad: anything a title would never legitimately contain.
    private static readonly Regex TitleBoundary = new(
        @"\b(S\d{1,2}(?:[\s._-]*E\d{1,3})?|\d{1,2}x\d{1,3}|season[\s._-]*\d{1,2}|complete|" +
        @"19\d{2}|20\d{2}|480p|576p|720p|1080p|2160p|4k|uhd|bluray|blu-ray|bdrip|brrip|web-?dl|webdl|" +
        @"web-?rip|hdtv|pdtv|remux|x264|x265|h\.?264|h\.?265|hevc|avc|hdr|hdr10|dv|amzn|nf|dsnp|hmax)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Title portion of a release name: text before the first structural marker, with separators
    /// normalized to spaces. E.g. "Lucky.Star.S01.1080p-GRP" → "Lucky Star"; "Lucky (2011) 1080p" → "Lucky".</summary>
    internal static string ExtractTitle(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        // Normalize scene separators (dots/underscores) to spaces FIRST — otherwise "\bcomplete\b" and
        // friends never match in underscore-separated names ("_" is a regex word char, so there's no
        // word boundary around it).
        var normalized = Regex.Replace(name, @"[._]+", " ");
        var m = TitleBoundary.Match(normalized);
        var head = m.Success ? normalized[..m.Index] : normalized;
        head = Regex.Replace(head, @"\s+", " ").Trim();
        // Strip trailing separators / dangling open brackets left by cutting before a "(2017)"-style marker.
        head = Regex.Replace(head, @"[\s\-_.\(\[\{:]+$", "").Trim();
        return head;
    }

    private const RegexOptions RxOpts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

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
