using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared.Releases;

/// <summary>Structured metadata parsed from a scene/p2p release name.</summary>
public record ParsedRelease
{
    /// <summary>The core title portion: everything before the first structural marker (season/episode,
    /// year, resolution, source, group…), separators normalized to spaces. Used for strict bidirectional
    /// title matching so an unrelated longer title (e.g. "Lucky Star") isn't accepted for a short request
    /// ("Lucky"). Empty when nothing precedes the first marker.</summary>
    public string Title { get; init; } = string.Empty;
    public int Resolution { get; init; }        // 480/720/1080/2160, 0 = unknown
    public ReleaseSource Source { get; init; }
    public string? Codec { get; init; }         // x264 / x265 / av1
    /// <summary>Any HDR variant present. Derived from <see cref="HdrFormat"/>; kept so existing scoring
    /// and preferences keep working unchanged.</summary>
    public bool Hdr { get; init; }
    public HdrFormat HdrFormat { get; init; }

    // ---- Audio / language / edition, for custom-format scoring ------------------------------------
    /// <summary>Spoken or subtitle languages mentioned in the name, lower-cased.</summary>
    public IReadOnlyList<string> Languages { get; init; } = Array.Empty<string>();
    /// <summary>True when the name advertises several audio languages (MULTi/DUAL).</summary>
    public bool MultiLanguage { get; init; }
    public bool Subbed { get; init; }
    public bool Dubbed { get; init; }

    /// <summary>Audio codec, normalised ("DTS-HD MA", "TrueHD", "EAC3"). Null when absent.</summary>
    public string? AudioCodec { get; init; }
    /// <summary>Music mastering depth (for example 16 or 24), when advertised.</summary>
    public int? AudioBitDepth { get; init; }
    /// <summary>Music sample rate in kHz (for example 44.1 or 96), when advertised.</summary>
    public double? AudioSampleRateKhz { get; init; }
    public bool LosslessAudio { get; init; }
    /// <summary>Channel layout such as "5.1". Null when absent.</summary>
    public string? AudioChannels { get; init; }
    /// <summary>Object-based audio (Atmos / DTS-X).</summary>
    public bool ObjectBasedAudio { get; init; }

    /// <summary>Cut/edition marker such as "Extended" or "Director's Cut".</summary>
    public string? Edition { get; init; }

    /// <summary>Lower-cased provenance flags present in the name (repack, internal, 10bit, ...).</summary>
    public IReadOnlySet<string> Flags { get; init; } = new HashSet<string>();
    public bool ProperOrRepack { get; init; }
    public string? Group { get; init; }

    // TV season/episode signals parsed from the name (works for every provider, not just EZTV).
    public int? Season { get; init; }           // season number when detectable (start season for a range)
    public int? SeasonEnd { get; init; }        // end season for a multi-season range pack (e.g. S01-S05), else null
    public int? Episode { get; init; }          // episode number for a single-episode release
    /// <summary>Every episode explicitly represented by one file/release name. A range is expanded, while
    /// chained forms such as S01E01E03 retain their non-contiguous coverage.</summary>
    public IReadOnlyList<int> EpisodeNumbers { get; init; } = Array.Empty<int>();
    // Episode span when the name declares a range (S01E01-E06). Lets a partial pack be told apart from a
    // full season; EpisodeNumbers remains authoritative when the declared episodes are non-contiguous.
    public int? EpisodeStart { get; init; }
    public int? EpisodeEnd { get; init; }
    public bool IsSeasonPack { get; init; }     // whole-season / complete-series / multi-season release
    // True only when the name explicitly said "complete"/"complete series" — as opposed to simply
    // failing to parse any season at all. Only releases with this set are pack-eligible for an
    // arbitrary requested season; a release that just didn't parse is NOT treated as matching everything.
    public bool LooksLikeCompleteSeries { get; init; }
    public int? Year { get; init; }             // 4-digit year token, when present (movies mainly)
}
