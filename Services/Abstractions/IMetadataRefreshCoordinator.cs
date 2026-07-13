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
}
