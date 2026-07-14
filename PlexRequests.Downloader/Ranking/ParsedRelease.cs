namespace PlexRequests.Downloader.Ranking;

public enum ReleaseSource { Unknown = 0, Cam = 1, Hdtv = 2, WebRip = 3, WebDl = 4, BluRay = 5, Remux = 6 }

/// <summary>Structured metadata parsed from a scene/p2p release name.</summary>
public record ParsedRelease
{
    public int Resolution { get; init; }        // 480/720/1080/2160, 0 = unknown
    public ReleaseSource Source { get; init; }
    public string? Codec { get; init; }         // x264 / x265 / av1
    public bool Hdr { get; init; }
    public bool ProperOrRepack { get; init; }
    public string? Group { get; init; }

    // TV season/episode signals parsed from the name (works for every provider, not just EZTV).
    public int? Season { get; init; }           // season number when detectable (start season for a range)
    public int? SeasonEnd { get; init; }        // end season for a multi-season range pack (e.g. S01-S05), else null
    public int? Episode { get; init; }          // episode number for a single-episode release
    public bool IsSeasonPack { get; init; }     // whole-season / complete-series / multi-season release
    // True only when the name explicitly said "complete"/"complete series" — as opposed to simply
    // failing to parse any season at all. Only releases with this set are pack-eligible for an
    // arbitrary requested season; a release that just didn't parse is NOT treated as matching everything.
    public bool LooksLikeCompleteSeries { get; init; }
    public int? Year { get; init; }             // 4-digit year token, when present (movies mainly)
}
