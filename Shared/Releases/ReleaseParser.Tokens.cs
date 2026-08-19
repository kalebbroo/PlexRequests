using System.Text.RegularExpressions;

namespace PlexRequestsHosted.Shared.Releases;

/// <summary>
/// The token vocabulary a release name is parsed against, and the title boundary generated from it.
///
/// Keeping these in one place and DERIVING the boundary regex from them is the point. The boundary decides
/// where a release's core title ends, so it has to know every structural token — otherwise a name like
/// <c>Movie.2019.MULTi.TrueFrench.DTS-HD.MA.5.1-GRP</c> only parses correctly by luck of some other token
/// matching first. A separate parser with its own token list would drift out of sync silently; generating
/// the boundary means adding an audio codec here automatically teaches the title extractor to stop there.
/// </summary>
public static partial class ReleaseTokens
{
    /// <summary>Spoken/subtitle language markers. Includes the multi-language and dub/sub conventions,
    /// which matter because a dubbed-only release is not what most people asked for.</summary>
    public static readonly string[] Languages =
    {
        "multi", "dual", "dl", "vostfr", "vff", "vfq", "vfi", "vf", "truefrench", "french",
        "german", "spanish", "castellano", "latino", "italian", "ita", "portuguese", "dutch",
        "nordic", "swedish", "danish", "norwegian", "finnish", "polish", "russian", "rus",
        "korean", "japanese", "jpn", "chinese", "hindi", "tamil", "telugu", "arabic",
        "subbed", "dubbed", "subfrench", "eng", "english"
    };

    /// <summary>
    /// Audio codecs, LONGEST FIRST. Order is load-bearing: matching "DTS" before "DTS-HD MA" would
    /// classify a lossless track as lossy. Separators are matched loosely because scene names use dots,
    /// spaces and dashes interchangeably.
    /// </summary>
    public static readonly (string Pattern, string Name)[] AudioCodecs =
    {
        (@"dts[\s._-]*hd[\s._-]*ma", "DTS-HD MA"),
        (@"dts[\s._-]*hd[\s._-]*hra", "DTS-HD HRA"),
        (@"dts[\s._-]*x", "DTS-X"),
        (@"dts[\s._-]*es", "DTS-ES"),
        (@"dts[\s._-]*hd", "DTS-HD"),
        (@"truehd", "TrueHD"),
        (@"atmos", "Atmos"),
        (@"dts", "DTS"),
        (@"e[\s._-]*ac[\s._-]*3", "EAC3"),
        (@"ddp?\+", "EAC3"),
        (@"ddp", "EAC3"),
        (@"ac[\s._-]*3", "AC3"),
        (@"flac", "FLAC"),
        (@"alac", "ALAC"),
        (@"(?:monkey'?s[\s._-]*audio|ape)", "APE"),
        (@"wav", "WAV"),
        (@"opus", "Opus"),
        (@"lpcm", "LPCM"),
        (@"pcm", "PCM"),
        (@"aac", "AAC"),
        (@"mp3", "MP3"),
        (@"dd", "AC3")
    };

    /// <summary>
    /// Channel layouts, as an explicit whitelist rather than a generic <c>\d\.\d</c>. That generic form
    /// collides with codec and version numbers — "H.264" would read as channels "2.6" — so only real
    /// layouts are accepted, and only after the audio codec has already been matched and removed.
    /// </summary>
    public static readonly string[] Channels = { "1.0", "2.0", "2.1", "5.0", "5.1", "6.1", "7.1" };

    /// <summary>Cut/edition markers.</summary>
    public static readonly (string Pattern, string Name)[] Editions =
    {
        (@"director'?s[\s._-]*cut", "Director's Cut"),
        (@"extended[\s._-]*(edition|cut)?", "Extended"),
        (@"special[\s._-]*edition", "Special Edition"),
        (@"ultimate[\s._-]*(edition|cut)?", "Ultimate"),
        (@"final[\s._-]*cut", "Final Cut"),
        (@"open[\s._-]*matte", "Open Matte"),
        (@"theatrical", "Theatrical"),
        (@"unrated", "Unrated"),
        (@"uncut", "Uncut"),
        (@"remastered", "Remastered"),
        (@"criterion", "Criterion"),
        (@"imax", "IMAX"),
        (@"hybrid", "Hybrid")
    };

    /// <summary>Miscellaneous quality/provenance flags worth scoring on.</summary>
    public static readonly string[] Flags =
    {
        "repack", "proper", "internal", "limited", "readnfo", "10bit", "8bit",
        "hdr10+", "hdr10", "hdr", "dv", "dovi", "hlg", "sdr", "remux", "upscaled", "scene"
    };

    // ---- Boundary generation ------------------------------------------------------------------------

    /// <summary>Tokens that are never part of a title, in regex-alternation form.</summary>
    private static IEnumerable<string> BoundaryAlternatives()
    {
        // Structural: season/episode, year, and the resolution/source vocabulary. These were the original
        // hand-maintained list and must keep matching exactly what they did before.
        yield return @"S\d{1,2}(?:[\s._-]*E\d{1,3})?";
        yield return @"\d{1,2}x\d{1,3}";
        yield return @"season[\s._-]*\d{1,2}";
        yield return "complete";
        yield return @"19\d{2}";
        yield return @"20\d{2}";
        foreach (var t in new[] { "480p", "576p", "720p", "1080p", "2160p", "4k", "uhd" }) yield return t;
        foreach (var t in new[] { "bluray", "blu-ray", "bdrip", "brrip", "web[\\s._-]*dl", "webdl", "web[\\s._-]*rip", "hdtv", "pdtv", "remux" }) yield return t;
        foreach (var t in new[] { "x264", "x265", @"h\.?264", @"h\.?265", "hevc", "avc" }) yield return t;
        foreach (var t in new[] { "hdr", "hdr10", "dv", "amzn", "nf", "dsnp", "hmax" }) yield return t;
        yield return @"(?:16|24|32)[\s._-]*bit";
        yield return @"(?:44\.1|48|88\.2|96|176\.4|192)[\s._-]*(?:khz|k)";

        // Newly contributed by this file. A title legitimately containing one of these is vanishingly rare
        // next to the number of release names where they mark the end of the title.
        foreach (var (pattern, _) in AudioCodecs) yield return pattern;
        foreach (var (pattern, _) in Editions) yield return pattern;
        foreach (var lang in Languages) yield return Regex.Escape(lang);
    }

    /// <summary>
    /// Where a release name's title ends. Generated from the tables above rather than hand-written, so the
    /// two can't disagree.
    /// </summary>
    public static readonly Regex TitleBoundary = new(
        @"\b(" + string.Join("|", BoundaryAlternatives()) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
