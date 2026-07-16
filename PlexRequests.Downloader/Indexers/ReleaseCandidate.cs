namespace PlexRequests.Downloader.Indexers;

/// <summary>A single downloadable release found by an indexer, before ranking.</summary>
public record ReleaseCandidate
{
    public required string ReleaseName { get; init; }
    public required string Magnet { get; init; }
    public string? InfoHash { get; init; }
    /// <summary>IMDb id the provider reported for this release (e.g. YTS imdb_code, EZTV imdb_id), if any.
    /// When present it's an authoritative identity check against the requested title's IMDb id.</summary>
    public string? ImdbId { get; init; }
    public int Seeders { get; init; }
    public int Leechers { get; init; }
    public long SizeBytes { get; init; }
    public string Source { get; init; } = string.Empty;
    /// <summary>Quality the provider reported directly (e.g. YTS "1080p"); null ⇒ parse from name.</summary>
    public string? QualityLabel { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }

    public double SizeGb => SizeBytes / 1024d / 1024d / 1024d;
}
