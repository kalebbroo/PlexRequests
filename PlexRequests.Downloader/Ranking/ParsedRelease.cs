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
}
