using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Shared.Releases;

public enum DownloadPlanKind
{
    None = 0,       // nothing acceptable found
    Single = 1,     // one release (a movie, or a whole-series single pick)
    SeasonPack = 2, // one or more full-season packs
    Episodes = 3    // individual episodes (fan-out)
}

/// <summary>
/// One selected release plus the season/episode it covers (for state tracking and imports).
/// <paramref name="NeededEpisodes"/> is the legacy single-season target list. <see cref="NeededEpisodeRefs"/>
/// is the canonical target contract and can span Plex seasons, which is required when one absolute/DVD/
/// digital-order pack satisfies several aired-order seasons. The worker uses it both to trim the transfer
/// and to prove exactly what may be imported.
/// </summary>
public record DownloadPlanItem(ReleaseCandidate Candidate, int? Season, int? Episode, bool IsPack, IReadOnlyList<int>? NeededEpisodes = null)
{
    public IReadOnlyList<EpisodeRef>? NeededEpisodeRefs { get; init; }

    /// <summary>Vertical resolution (pixel height) of the chosen release, per the ranker. 0 = unknown.
    /// Carried through to the imported-file audit rows so achieved quality / cutoff can be evaluated.</summary>
    public int Resolution { get; init; }
}

/// <summary>
/// The downloader's decision for a job: which releases to add. May be a single release (movie / pack)
/// or several (season packs, or individual episodes when no acceptable pack exists).
/// <paramref name="CoversAllTargets"/> is false when the planner deliberately downloads the subset that
/// is currently findable. The pipeline must preserve that distinction instead of reporting the whole
/// request Available merely because every selected transfer imported successfully.
/// </summary>
public record DownloadPlan(DownloadPlanKind Kind, IReadOnlyList<DownloadPlanItem> Items, bool CoversAllTargets = true)
{
    public static readonly DownloadPlan None = new(DownloadPlanKind.None, Array.Empty<DownloadPlanItem>(), false);
    public bool IsEmpty => Kind == DownloadPlanKind.None || Items.Count == 0;
}
