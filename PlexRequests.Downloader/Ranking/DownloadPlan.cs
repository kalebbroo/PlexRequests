using PlexRequests.Downloader.Indexers;

namespace PlexRequests.Downloader.Ranking;

public enum DownloadPlanKind
{
    None = 0,       // nothing acceptable found
    Single = 1,     // one release (a movie, or a whole-series single pick)
    SeasonPack = 2, // one or more full-season packs
    Episodes = 3    // individual episodes (fan-out)
}

/// <summary>
/// One selected release plus the season/episode it covers (for state tracking and imports).
/// <paramref name="NeededEpisodes"/> is set only when a season pack was chosen to satisfy a specific
/// subset of episodes (episode-level request, or a partially-missing season): it lists the episode
/// numbers we actually want, so the download client can skip the pack's other files and the importer
/// can avoid re-importing episodes already on Plex. Null means "take the whole thing" (movie, single
/// episode, or a pack covering a fully-missing season).
/// </summary>
public record DownloadPlanItem(ReleaseCandidate Candidate, int? Season, int? Episode, bool IsPack, IReadOnlyList<int>? NeededEpisodes = null);

/// <summary>
/// The downloader's decision for a job: which releases to add. May be a single release (movie / pack)
/// or several (season packs, or individual episodes when no acceptable pack exists).
/// </summary>
public record DownloadPlan(DownloadPlanKind Kind, IReadOnlyList<DownloadPlanItem> Items)
{
    public static readonly DownloadPlan None = new(DownloadPlanKind.None, Array.Empty<DownloadPlanItem>());
    public bool IsEmpty => Kind == DownloadPlanKind.None || Items.Count == 0;
}
