using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Indexers;

/// <summary>Runs every provider that supports the job's media type and merges the candidates.</summary>
public class IndexerClient(IEnumerable<IIndexerProvider> providers, ILogger<IndexerClient> logger) : IIndexerClient
{
    private readonly IReadOnlyList<IIndexerProvider> _providers = providers.ToList();
    private readonly ILogger<IndexerClient> _logger = logger;

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(FulfillmentJobDto job, CancellationToken ct)
    {
        // Anime-only sources (Nyaa) are skipped unless the job was classified as anime — this stops a plain
        // movie/TV request from ever matching an anime release with a coincidentally-overlapping title.
        var applicable = _providers
            .Where(p => p.Supports(job.MediaType))
            .Where(p => !p.AnimeOnly || job.IsAnime)
            .ToList();
        if (applicable.Count == 0)
        {
            _logger.LogWarning("No indexer supports media type {Type} for \"{Title}\" (anime={IsAnime})", job.MediaType, job.Title, job.IsAnime);
            return Array.Empty<ReleaseCandidate>();
        }

        var results = await Task.WhenAll(applicable.Select(async p =>
        {
            try
            {
                var found = await p.SearchAsync(job, ct);
                _logger.LogInformation("{Provider}: {Count} candidate(s) for \"{Title}\"", p.Name, found.Count, job.Title);
                return found;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Indexer {Provider} failed for \"{Title}\"", p.Name, job.Title);
                return (IReadOnlyList<ReleaseCandidate>)Array.Empty<ReleaseCandidate>();
            }
        }));

        return results.SelectMany(r => r).ToList();
    }
}
