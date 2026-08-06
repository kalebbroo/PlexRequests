using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Abstractions;

/// <summary>
/// Singleton that performs fire-and-forget background refreshes of stale metadata-cache rows, off the
/// request path. Dedupes concurrent requests for the same key so a stampede of readers triggers one
/// live fetch. Resolves the raw inner provider (not the caching decorator) to avoid recursion.
/// </summary>
public interface IMetadataRefreshCoordinator
{
    void QueueDetail(MediaType mediaType, int tmdbId);
    void QueueEpisodes(int showTmdbId, int seasonNumber);

    /// <summary>
    /// Fetch fresh metadata and WAIT for it, rather than queueing a background refresh and returning the
    /// stale copy. The monitor needs this: with a 6-hour detail TTL and a 12-hour episode TTL, a newly
    /// aired episode could stay invisible for most of a day purely because nothing ever forced a fetch.
    /// Shares the same in-flight dedup as the queue methods, so a concurrent background refresh isn't
    /// duplicated.
    /// </summary>
    Task<MediaDetailDto?> RefreshDetailNowAsync(MediaType mediaType, int tmdbId);

    Task<List<EpisodeDto>> RefreshEpisodesNowAsync(int showTmdbId, int seasonNumber);
}
