using PlexRequests.Downloader.Indexers;

namespace PlexRequests.Downloader.Ranking;

public enum DownloadPlanKind
{
    None = 0,       // nothing acceptable found
    Single = 1,     // one release (a movie, or a whole-series single pick)
    SeasonPack = 2, // one or more full-season packs
    Episodes = 3    // individual episodes (fan-out)
}

/// <summary>One selected release plus the season/episode it covers (for state tracking and imports).</summary>
public record DownloadPlanItem(ReleaseCandidate Candidate, int? Season, int? Episode, bool IsPack);

/// <summary>
/// The downloader's decision for a job: which releases to add. May be a single release (movie / pack)
/// or several (season packs, or individual episodes when no acceptable pack exists).
/// </summary>
public record DownloadPlan(DownloadPlanKind Kind, IReadOnlyList<DownloadPlanItem> Items)
{
    public static readonly DownloadPlan None = new(DownloadPlanKind.None, Array.Empty<DownloadPlanItem>());
    public bool IsEmpty => Kind == DownloadPlanKind.None || Items.Count == 0;
}
