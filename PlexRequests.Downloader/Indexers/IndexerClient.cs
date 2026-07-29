using System.Diagnostics;
using PlexRequests.Downloader.Api;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Indexers;

/// <summary>One provider's outcome for one search pass — feeds the defer-reason summary and the admin panel.</summary>
public sealed record ProviderSearchOutcome(string Provider, bool Success, int Count, string? Error, int LatencyMs);

/// <summary>Merged candidates plus the per-provider breakdown of where they came from (or didn't).</summary>
public sealed record IndexerSearchResult(
    IReadOnlyList<ReleaseCandidate> Candidates,
    IReadOnlyList<ProviderSearchOutcome> Providers)
{
    public static readonly IndexerSearchResult Empty =
        new(Array.Empty<ReleaseCandidate>(), Array.Empty<ProviderSearchOutcome>());

    /// <summary>Human-readable per-provider summary, e.g. "EZTV: 0, PirateBay: 12, 1337x: error".
    /// Shown in the admin Missing panel via the deferral reason, so "why is nothing found" is
    /// answerable without downloader logs.</summary>
    public string Summary => Providers.Count == 0
        ? "no indexers enabled for this media type"
        : string.Join(", ", Providers.Select(o => o.Success ? $"{o.Provider}: {o.Count}" : $"{o.Provider}: error"));
}

/// <summary>
/// Runs every provider that supports the job's media type (and that the admin hasn't disabled — see the
/// Indexers panel), merges the candidates, and reports each provider's outcome back to the web app as
/// rolling health telemetry.
/// </summary>
public class IndexerClient(
    IEnumerable<IIndexerProvider> providers,
    IIndexerSettingsProvider settings,
    IPlexRequestsApiClient api,
    ILogger<IndexerClient> logger) : IIndexerClient
{
    private readonly IReadOnlyList<IIndexerProvider> _providers = providers.ToList();
    private readonly IIndexerSettingsProvider _settings = settings;
    private readonly IPlexRequestsApiClient _api = api;
    private readonly ILogger<IndexerClient> _logger = logger;

    public async Task<IndexerSearchResult> SearchAsync(FulfillmentJobDto job, CancellationToken ct)
    {
        await _settings.RefreshAsync(ct); // pick up admin enable/priority changes (throttled internally)

        // Anime-only sources (Nyaa) are skipped unless the job was classified as anime — this stops a plain
        // movie/TV request from ever matching an anime release with a coincidentally-overlapping title.
        var applicable = _providers
            .Where(p => p.Supports(job.MediaType))
            .Where(p => !p.AnimeOnly || job.IsAnime)
            .Where(p => _settings.IsEnabled(p.Name))
            .ToList();
        if (applicable.Count == 0)
        {
            _logger.LogWarning("No enabled indexer supports media type {Type} for \"{Title}\" (anime={IsAnime})", job.MediaType, job.Title, job.IsAnime);
            return IndexerSearchResult.Empty;
        }

        var searchedAt = DateTime.UtcNow;
        var results = await Task.WhenAll(applicable.Select(async p =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var found = await p.SearchAsync(job, ct);
                sw.Stop();
                _logger.LogInformation("{Provider}: {Count} candidate(s) for \"{Title}\" in {Ms}ms", p.Name, found.Count, job.Title, sw.ElapsedMilliseconds);
                return (Candidates: found, Outcome: new ProviderSearchOutcome(p.Name, true, found.Count, null, (int)sw.ElapsedMilliseconds));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sw.Stop();
                _logger.LogWarning(ex, "Indexer {Provider} failed for \"{Title}\"", p.Name, job.Title);
                return (Candidates: (IReadOnlyList<ReleaseCandidate>)Array.Empty<ReleaseCandidate>(),
                        Outcome: new ProviderSearchOutcome(p.Name, false, 0, ex.Message, (int)sw.ElapsedMilliseconds));
            }
        }));

        var outcomes = results.Select(r => r.Outcome).ToList();

        // Health telemetry for the admin Indexers panel — best-effort, never blocks or fails a search.
        try
        {
            await _api.ReportIndexerStatusAsync(outcomes.Select(o => new IndexerStatusReportDto
            {
                Name = o.Provider,
                Success = o.Success,
                ResultCount = o.Count,
                Error = o.Error,
                LatencyMs = o.LatencyMs,
                SearchedAt = searchedAt
            }).ToList(), ct);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Indexer status report skipped"); }

        return new IndexerSearchResult(results.SelectMany(r => r.Candidates).ToList(), outcomes);
    }
}
