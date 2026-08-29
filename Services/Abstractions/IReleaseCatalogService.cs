using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequestsHosted.Services.Abstractions;

public interface IReleaseCatalogService
{
    Task<CatalogUpsertResultDto> UpsertBatchAsync(CatalogBatchDto batch, CancellationToken cancellationToken);
    Task<CatalogHydrationPageDto> GetPendingHydrationAsync(
        int indexerId,
        string source,
        long afterId,
        int limit,
        CancellationToken cancellationToken);
    Task<bool> MarkHydrationFailedAsync(
        CatalogHydrationFailureDto failure,
        CancellationToken cancellationToken);
    Task<CatalogStatsDto> GetStatsAsync(CancellationToken cancellationToken);
    Task<CatalogCheckpointDto> GetCheckpointAsync(int indexerId, CancellationToken cancellationToken);
    Task<CatalogCheckpointDto> ReportFailureAsync(CatalogFailureDto failure, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(CatalogQueryDto query, CancellationToken cancellationToken);
    Task<CatalogBrowsePageDto> BrowseAsync(CatalogBrowseQueryDto query, CancellationToken cancellationToken);
}
