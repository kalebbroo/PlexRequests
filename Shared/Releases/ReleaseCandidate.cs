using PlexRequestsHosted.Shared.Enums;
namespace PlexRequestsHosted.Shared.Releases;

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

    /// <summary>Which configured indexer produced this. Replaces matching on the source name, which broke
    /// as soon as several Torznab endpoints existed — they all reported the same string.</summary>
    public int IndexerId { get; init; }
    /// <summary>Display name of that indexer, for logs and the admin UI.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Quality the provider reported directly (e.g. YTS "1080p"); null ⇒ parse from name.</summary>
    public string? QualityLabel { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }

    /// <summary>When the indexer says the release was published. Null when it doesn't report one.</summary>
    public DateTime? PublishDate { get; init; }

    /// <summary>
    /// False when the indexer didn't actually report seeders/size and the zeros are absence, not fact. The
    /// HTML scrapers frequently fail to parse both, and treating that as "0 seeders, 0 bytes" silently
    /// discarded their results against the minimum-size and minimum-seeder gates.
    /// </summary>
    public bool SeedersKnown { get; init; } = true;
    public bool SizeKnown { get; init; } = true;

    public double SizeGb => SizeBytes / 1024d / 1024d / 1024d;

    /// <summary>Age in days, when the indexer reported a publish date.</summary>
    public double? AgeDays => PublishDate is DateTime d ? (DateTime.UtcNow - d).TotalDays : null;
}
