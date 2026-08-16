using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// Optional incremental-ingestion capability. Search-only implementations do not implement it and never
/// appear in the ingestion worker's adapter map.
/// </summary>
public interface IReleaseFeedSource
{
    string Key { get; }
    Task<ReleaseFeedPage> FetchAsync(
        IndexerConfigDto indexer,
        string? cursor,
        int limit,
        CancellationToken cancellationToken);
}

public sealed record ReleaseFeedPage(IReadOnlyList<CatalogItemDto> Items, string? NextCursor);

/// <summary>
/// Selects a bounded, gap-free slice from a newest-first listing. When more than one batch arrived since
/// the previous cursor, the oldest unseen slice is committed first; subsequent polls walk forward until
/// they reach the newest item instead of skipping the middle.
/// </summary>
internal static class ReleaseFeedCursor
{
    public static ReleaseFeedPage Select(
        IReadOnlyList<CatalogItemDto> newestFirst,
        string? cursor,
        int limit)
    {
        var bounded = Math.Clamp(limit, 1, 1000);
        // Aggregators can surface the same torrent through several trackers in one response. Collapse those
        // sightings before slicing so a duplicate ExternalId cannot poison a batch and pin its checkpoint.
        var unique = newestFirst
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalId))
            .DistinctBy(x => x.ExternalId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unique.Count == 0) return new ReleaseFeedPage(Array.Empty<CatalogItemDto>(), cursor);
        if (string.IsNullOrWhiteSpace(cursor))
        {
            var initial = unique.Take(bounded).ToList();
            return new ReleaseFeedPage(initial, initial.FirstOrDefault()?.ExternalId);
        }

        var unseen = unique
            .TakeWhile(x => !string.Equals(x.ExternalId, cursor, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (unseen.Count == 0) return new ReleaseFeedPage(Array.Empty<CatalogItemDto>(), cursor);
        var selected = unseen.Count <= bounded ? unseen : unseen.TakeLast(bounded).ToList();
        return new ReleaseFeedPage(selected, selected[0].ExternalId);
    }
}
