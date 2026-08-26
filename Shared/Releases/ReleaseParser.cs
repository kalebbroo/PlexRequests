using System.Text.RegularExpressions;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared.Releases;

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
        var original = releaseName ?? string.Empty;
        var name = NormalizeForParsing(original);

        int resolution =
            Rx(name, @"\b(2160p|4k|uhd)\b") ? 2160 :
            Rx(name, @"\b1080p\b") ? 1080 :
            Rx(name, @"\b720p\b") ? 720 :
            Rx(name, @"\b(480p|576p|sd)\b") ? 480 : 0;

        ReleaseSource source =
            Rx(name, @"\bremux\b") ? ReleaseSource.Remux :
            Rx(name, @"\b(bluray|blu-ray|bdrip|brrip)\b") ? ReleaseSource.BluRay :
            Rx(name, @"\b(web[\s._-]*dl|webdl|amzn|nf|dsnp|hmax)\b") ? ReleaseSource.WebDl :
            Rx(name, @"\bweb[\s._-]*rip\b") ? ReleaseSource.WebRip :
            Rx(name, @"\b(hdtv|pdtv)\b") ? ReleaseSource.Hdtv :
            Rx(name, @"\b(cam|ts|telesync|hdcam)\b") ? ReleaseSource.Cam :
            ReleaseSource.Unknown;

        string? codec =
            Rx(name, @"\b(x265|h\.?265|hevc)\b") ? "x265" :
            Rx(name, @"\b(x264|h\.?264|avc)\b") ? "x264" :
            Rx(name, @"\bav1\b") ? "av1" : null;

        var hdrFormat = ParseHdr(name);
        bool hdr = hdrFormat is HdrFormat.Hdr10 or HdrFormat.Hdr10Plus or HdrFormat.DolbyVision or HdrFormat.Hlg;
        bool proper = Rx(name, @"\b(proper|repack)\b");

        var (audioCodec, audioChannels, objectAudio) = ParseAudio(name);
        var (audioBitDepth, audioSampleRateKhz) = ParseMusicAudio(name);
        var languages = ParseLanguages(name);
        var edition = ParseEdition(name);
        var flags = ParseFlags(name);

        // Group: trailing "-GROUP" token.
        string? group = null;
        var m = GroupRegex().Match(original);
        if (m.Success) group = m.Groups[1].Value;

        var (season, seasonEnd, episode, episodeNumbers, isPack, looksLikeComplete, epStart, epEnd) =
            ParseSeasonEpisode(name);

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
            HdrFormat = hdrFormat,
            AudioCodec = audioCodec,
            AudioBitDepth = audioBitDepth,
            AudioSampleRateKhz = audioSampleRateKhz,
            LosslessAudio = audioCodec is "FLAC" or "ALAC" or "APE" or "WAV" or "PCM" or "LPCM",
            AudioChannels = audioChannels,
            ObjectBasedAudio = objectAudio,
            Languages = languages,
            MultiLanguage = languages.Contains("multi") || languages.Contains("dual"),
            Subbed = languages.Contains("subbed") || languages.Contains("vostfr") || languages.Contains("subfrench"),
            Dubbed = languages.Contains("dubbed"),
            Edition = edition,
            Flags = flags,
            ProperOrRepack = proper,
            Group = group,
            Season = season,
            SeasonEnd = seasonEnd,
            Episode = episode,
            EpisodeNumbers = episodeNumbers,
            EpisodeStart = epStart,
            EpisodeEnd = epEnd,
            IsSeasonPack = isPack,
            LooksLikeCompleteSeries = looksLikeComplete,
            Year = year
        };
    }

    private static string NormalizeForParsing(string? releaseName)
    {
        if (string.IsNullOrWhiteSpace(releaseName)) return string.Empty;

        // Canonicalize underscores to spaces and collapse whitespace.
        // Underscores are regex word chars, so they break many \b boundaries used by parsing.
        var normalized = releaseName.Replace('_', ' ');
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Extract season/episode and whether the release is a whole-season/complete pack, from the name.
    /// A single episode (SxxExx or NxNN) sets Season+Episode; a pack (Sxx with no Exx, "Season N",
    /// "Complete", or a multi-season range Sxx-Sxx) sets IsSeasonPack (+Season/SeasonEnd when named).
    /// <paramref name="name"/> failing to parse ANY season at all is distinct from an explicit "complete"
    /// match — only the latter sets <c>looksLikeCompleteSeries</c>, so a release that simply didn't parse
    /// isn't later treated as matching every requested season.
    /// </summary>
    private static (int? season, int? seasonEnd, int? episode, IReadOnlyList<int> episodeNumbers,
        bool isPack, bool looksLikeCompleteSeries, int? epStart, int? epEnd) ParseSeasonEpisode(string name)
    {
        // Episode RANGE first — it must beat the single-episode pattern, which would otherwise match the
        // start of "S01E01-E06" and report a one-episode release. Telling a partial pack from a full season
        // is what stops a six-episode pack being accepted for a thirteen-episode season.
        var range = Regex.Match(name,
            @"\bS(\d{1,2})[\s._-]*E(\d{1,3})[\s._-]*-[\s._-]*(?:S(\d{1,2})[\s._-]*)?(?:E)?(\d{1,3})\b",
            RxOpts);
        if (range.Success)
        {
            int s = int.Parse(range.Groups[1].Value);
            int a = int.Parse(range.Groups[2].Value), b = int.Parse(range.Groups[4].Value);
            var endSeasonMatches = !range.Groups[3].Success || int.Parse(range.Groups[3].Value) == s;
            if (endSeasonMatches && b > a)
                return (s, null, null, Enumerable.Range(a, b - a + 1).ToList(), true, false, a, b);
        }

        // One physical file can declare multiple non-contiguous episodes (S01E01E03) as well as the
        // common compact contiguous form (S01E01E02). Preserve the exact set instead of widening it to a
        // range and falsely claiming that E02 is covered by the first example.
        var chained = Regex.Match(name, @"\bS(\d{1,2})((?:[\s._-]*E\d{1,3}){2,})\b", RxOpts);
        if (chained.Success)
        {
            int s = int.Parse(chained.Groups[1].Value);
            var episodes = Regex.Matches(chained.Groups[2].Value, @"E(\d{1,3})", RxOpts)
                .Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList();
            if (episodes.Count > 1)
                return (s, null, null, episodes, true, false, episodes.Min(), episodes.Max());
        }

        // Single episode: S01E02 / S1E2 / 1x02.
        var ep = Regex.Match(name, @"\bS(\d{1,2})[\s._-]*E(\d{1,3})\b", RxOpts);
        if (ep.Success)
        {
            var value = int.Parse(ep.Groups[2].Value);
            return (int.Parse(ep.Groups[1].Value), null, value, [value], false, false, null, null);
        }
        var alt = Regex.Match(name, @"\b(\d{1,2})x(\d{1,3})\b", RxOpts);
        if (alt.Success)
        {
            var value = int.Parse(alt.Groups[2].Value);
            return (int.Parse(alt.Groups[1].Value), null, value, [value], false, false, null, null);
        }

        // Anime absolute order: "Show - 13" / "Show - 13v2". Season zero is a deliberate source-order
        // sentinel, never a Plex destination; an active series mapping must translate it before use.
        var absolute = Regex.Match(name, @"(?:^|[\s._])-[\s._]*(\d{1,3})(?:v\d+)?(?=[\s._\[\(\-]|$)", RxOpts);
        if (absolute.Success)
        {
            var value = int.Parse(absolute.Groups[1].Value);
            if (value > 0) return (0, null, value, [value], false, false, null, null);
        }

        // No explicit episode ⇒ look for pack signals.
        var multi = Regex.Match(name, @"\bS(\d{1,2})[\s._-]*-[\s._-]*S(\d{1,2})\b", RxOpts); // S01-S05
        if (multi.Success) return (int.Parse(multi.Groups[1].Value), int.Parse(multi.Groups[2].Value), null, [], true, false, null, null);

        var seasonWord = Regex.Match(name, @"\bseason[\s._-]*(\d{1,2})\b", RxOpts);          // Season 1
        if (seasonWord.Success) return (int.Parse(seasonWord.Groups[1].Value), null, null, [], true, false, null, null);

        var sOnly = Regex.Match(name, @"\bS(\d{1,2})\b(?![\s._-]*E\d)", RxOpts);             // S01 (no Exx)
        if (sOnly.Success) return (int.Parse(sOnly.Groups[1].Value), null, null, [], true, false, null, null);

        if (Rx(name, @"\b(complete|complete[\s._-]*series)\b")) return (null, null, null, [], true, true, null, null); // whole-series pack, explicit

        return (null, null, null, [], false, false, null, null);
    }

    // Where the title ends. Generated from ReleaseTokens rather than hand-maintained, so a token added
    // for scoring automatically becomes a title boundary too.
    private static Regex TitleBoundary => ReleaseTokens.TitleBoundary;

    // ---- Audio / language / edition ---------------------------------------------------------------

    /// <summary>
    /// Audio codec and channel layout. The codec is matched FIRST and its text removed before looking for
    /// channels, because the obvious channel pattern collides with codec names — searching a raw
    /// "H.264" for a digit-dot-digit yields "2.6".
    /// </summary>
    internal static (string? Codec, string? Channels, bool ObjectBased) ParseAudio(string name)
    {
        string? codec = null;
        var remaining = name;

        // Longest-first: matching "DTS" before "DTS-HD MA" would call a lossless track lossy.
        foreach (var (pattern, display) in ReleaseTokens.AudioCodecs)
        {
            var m = Regex.Match(remaining, @"\b" + pattern + @"\b", RxOpts);
            if (!m.Success) continue;
            codec = display;
            remaining = remaining.Remove(m.Index, m.Length);
            break;
        }

        string? channels = null;
        foreach (var layout in ReleaseTokens.Channels)
        {
            // Guard against digits only, not dots. Removing the codec leaves a separator behind
            // ("DTS-HD.MA.5.1" -> "..5.1"), so excluding a preceding dot would reject the very layout we
            // just made findable. The whitelist itself is what prevents "H.264" being read as channels.
            if (Regex.IsMatch(remaining, @"(?<!\d)" + Regex.Escape(layout) + @"(?!\d)", RxOpts))
            {
                channels = layout;
                break;
            }
        }

        bool objectBased = Rx(name, @"\b(atmos|dts[\s._-]*x)\b");
        return (codec, channels, objectBased);
    }

    internal static (int? BitDepth, double? SampleRateKhz) ParseMusicAudio(string name)
    {
        var bit = Regex.Match(name, @"\b(16|24|32)[\s._-]*bit\b", RxOpts);
        var rate = Regex.Match(name, @"\b(44\.1|48|88\.2|96|176\.4|192)[\s._-]*(?:khz|k)\b", RxOpts);
        return (
            bit.Success ? int.Parse(bit.Groups[1].Value) : null,
            rate.Success && double.TryParse(rate.Groups[1].Value,
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedRate) ? parsedRate : null);
    }

    internal static List<string> ParseLanguages(string name)
    {
        var found = new List<string>();
        foreach (var lang in ReleaseTokens.Languages)
            if (Rx(name, @"\b" + Regex.Escape(lang) + @"\b")) found.Add(lang.ToLowerInvariant());
        return found;
    }

    internal static string? ParseEdition(string name)
    {
        foreach (var (pattern, display) in ReleaseTokens.Editions)
            if (Rx(name, @"\b" + pattern + @"\b")) return display;
        return null;
    }

    internal static HashSet<string> ParseFlags(string name)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var flag in ReleaseTokens.Flags)
            if (Rx(name, @"(?<![\w])" + Regex.Escape(flag) + @"(?![\w])")) set.Add(flag.ToLowerInvariant());
        return set;
    }

    /// <summary>Most specific HDR variant present. Dolby Vision outranks HDR10+ outranks HDR10.</summary>
    internal static HdrFormat ParseHdr(string name)
    {
        if (Rx(name, @"\b(dv|dovi|dolby[\s._-]*vision)\b")) return HdrFormat.DolbyVision;
        if (Rx(name, @"hdr10\+")) return HdrFormat.Hdr10Plus;
        if (Rx(name, @"\bhdr10\b")) return HdrFormat.Hdr10;
        if (Rx(name, @"\bhdr\b")) return HdrFormat.Hdr10;
        if (Rx(name, @"\bhlg\b")) return HdrFormat.Hlg;
        if (Rx(name, @"\bsdr\b")) return HdrFormat.Sdr;
        return HdrFormat.None;
    }

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
        var absolute = Regex.Match(normalized, @"\s+-\s+\d{1,3}(?:v\d+)?(?=\s|$)", RxOpts);
        var boundary = new[] { m.Success ? m.Index : int.MaxValue, absolute.Success ? absolute.Index : int.MaxValue }.Min();
        var head = boundary == int.MaxValue ? normalized : normalized[..boundary];
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

    [GeneratedRegex(@"(?:-|_)([A-Za-z0-9]+)(?:\.[A-Za-z0-9]+)?$")]
    private static partial Regex GroupRegex();
}
