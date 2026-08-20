using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Api;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Indexers;

/// <summary>One indexer's outcome for one search pass — feeds the defer-reason summary and the admin panel.</summary>
public sealed record ProviderSearchOutcome(int IndexerId, string Provider, bool Success, int Count, string? Error, int LatencyMs,
    IndexerBlockReason BlockReason = IndexerBlockReason.None);

/// <summary>Merged candidates plus the per-indexer breakdown of where they came from (or didn't).</summary>
public sealed record IndexerSearchResult(
    IReadOnlyList<ReleaseCandidate> Candidates,
    IReadOnlyList<ProviderSearchOutcome> Providers)
{
    public static readonly IndexerSearchResult Empty =
        new(Array.Empty<ReleaseCandidate>(), Array.Empty<ProviderSearchOutcome>());

    /// <summary>Human-readable per-indexer summary, e.g. "EZTV: 0, PirateBay: 12, 1337x: error".
    /// Shown in the admin Missing panel via the deferral reason, so "why is nothing found" is
    /// answerable without downloader logs.</summary>
    public string Summary => Providers.Count == 0
        ? "no indexers enabled for this media type"
        : string.Join(", ", Providers.Select(o => o.Success ? $"{o.Provider}: {o.Count}"
            : o.BlockReason != IndexerBlockReason.None ? $"{o.Provider}: {o.BlockReason}"
            : $"{o.Provider}: error"));
}

/// <summary>
/// Runs every configured indexer that can serve the job, merges the results, and reports each one's outcome
/// back to the web app as rolling health telemetry.
///
/// Fan-out is driven by the admin's indexer ROWS, not by what happens to be registered in DI: an
/// implementation is looked up per row by its key, so one Torznab implementation serves however many
/// endpoints exist. Concurrency is bounded, results are cached per (indexer, query) and deduplicated across
/// indexers by info hash — none of which the previous unbounded, uncached, un-deduplicated version did.
/// </summary>
public class IndexerClient(
    IEnumerable<IIndexerImplementation> implementations,
    IEnumerable<IAcquisitionCandidateSource> acquisitionSources,
    IIndexerSettingsProvider settings,
    IIndexerRateLimiter rateLimiter,
    IMemoryCache cache,
    IPlexRequestsApiClient api,
    IOptions<CatalogWorkerOptions> catalogOptions,
    ILogger<IndexerClient> logger) : IIndexerClient
{
    // Kept modest: these all egress through one VPN tunnel, and several are public sites that respond
    // badly to a burst. Per-indexer rate limits sit underneath this.
    private const int MaxConcurrentIndexers = 6;

    private readonly Dictionary<string, IIndexerImplementation> _byKey =
        implementations.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);

    public async Task<IndexerSearchResult> SearchAsync(
        FulfillmentJobDto job,
        CancellationToken ct,
        IndexerSearchPurpose purpose = IndexerSearchPurpose.Automatic)
    {
        await settings.RefreshAsync(ct); // pick up admin changes (throttled internally)
        var catalogCandidates = catalogOptions.Value.Enabled && catalogOptions.Value.UseForSearch
            ? await SearchCatalogWithBudgetAsync(job, ct)
            : Array.Empty<ReleaseCandidate>();
        var directResults = await SearchAcquisitionSourcesAsync(job, ct);
        var directCandidates = directResults.SelectMany(x => x.Candidates).ToList();

        var now = DateTime.UtcNow;
        var applicable = settings.All
            .Where(i => IsEnabledFor(i, purpose, now))
            .Where(i => i.Supports(job.MediaType))
            // Anime-only sources (Nyaa) are skipped unless the job was classified as anime, so a plain
            // movie/TV request never matches an anime release with a coincidentally overlapping title.
            .Where(i => !i.AnimeOnly || job.IsAnime)
            .Where(i => _byKey.ContainsKey(i.Implementation))
            .ToList();

        foreach (var unknown in settings.All.Where(i => i.Enabled && !_byKey.ContainsKey(i.Implementation)))
            logger.LogWarning("Indexer \"{Name}\" has no implementation for \"{Impl}\"; skipping", unknown.Name, unknown.Implementation);

        if (applicable.Count == 0)
        {
            if (catalogCandidates.Count == 0 && directCandidates.Count == 0)
                logger.LogWarning("No enabled indexer supports media type {Type} for \"{Title}\" (anime={IsAnime})",
                    job.MediaType, job.Title, job.IsAnime);
            return new IndexerSearchResult(Deduplicate(catalogCandidates.Concat(directCandidates)),
                directResults.Select(x => x.Outcome).ToList());
        }

        var searchedAt = DateTime.UtcNow;
        var results = new List<(IReadOnlyList<ReleaseCandidate> Candidates, ProviderSearchOutcome Outcome)>();
        var gate = new object();

        await Parallel.ForEachAsync(applicable,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentIndexers, CancellationToken = ct },
            async (indexer, token) =>
            {
                var outcome = await SearchOneAsync(indexer, job, token);
                lock (gate) results.Add(outcome);
            });

        var outcomes = directResults.Select(x => x.Outcome).Concat(results.Select(r => r.Outcome)).ToList();
        await ReportHealthAsync(outcomes, searchedAt, ct);
        if (catalogOptions.Value.Enabled)
            await WriteThroughAsync(results.SelectMany(x => x.Candidates)
                .Where(x => x.Acquisition.Protocol == AcquisitionProtocol.Torrent), job.MediaType, searchedAt, ct);

        var merged = Deduplicate(catalogCandidates.Concat(directCandidates).Concat(results.SelectMany(r => r.Candidates)));
        return new IndexerSearchResult(merged, outcomes);
    }

    private async Task<List<(IReadOnlyList<ReleaseCandidate> Candidates, ProviderSearchOutcome Outcome)>>
        SearchAcquisitionSourcesAsync(FulfillmentJobDto job, CancellationToken ct)
    {
        var results = new List<(IReadOnlyList<ReleaseCandidate>, ProviderSearchOutcome)>();
        foreach (var source in acquisitionSources.Where(x => x.AppliesTo(job)))
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var candidates = await source.SearchAsync(job, ct);
                results.Add((candidates, new ProviderSearchOutcome(0, source.Name, true, candidates.Count, null,
                    (int)sw.ElapsedMilliseconds)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Acquisition source {Source} failed for {Title}", source.Name, job.Title);
                results.Add((Array.Empty<ReleaseCandidate>(), new ProviderSearchOutcome(0, source.Name, false, 0,
                    ex.Message, (int)sw.ElapsedMilliseconds)));
            }
        }
        return results;
    }

    internal static bool IsEnabledFor(IndexerConfigDto indexer, IndexerSearchPurpose purpose, DateTime now) =>
        indexer.Enabled
        && (indexer.SearchCircuitOpenUntil is null || indexer.SearchCircuitOpenUntil <= now)
        && (purpose switch
        {
            IndexerSearchPurpose.Interactive => indexer.EnableInteractiveSearch,
            IndexerSearchPurpose.Monitoring => indexer.EnableIngestion,
            _ => indexer.EnableAutomaticSearch
        });

    private async Task<IReadOnlyList<ReleaseCandidate>> SearchCatalogWithBudgetAsync(
        FulfillmentJobDto job,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var candidates = await api.SearchCatalogAsync(job, budget.Token);
            if (candidates is { Count: > 0 })
                logger.LogInformation("Catalog: {Count} candidate(s) for \"{Title}\"", candidates.Count, job.Title);
            return candidates ?? Array.Empty<ReleaseCandidate>();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Catalog search exceeded its two-second budget for {Title}", job.Title);
            return Array.Empty<ReleaseCandidate>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Catalog search skipped for {Title}", job.Title);
            return Array.Empty<ReleaseCandidate>();
        }
    }

    public async Task<IndexerCapabilitiesDto> TestAsync(int indexerId, CancellationToken ct)
    {
        await settings.RefreshAsync(ct);

        var indexer = settings.All.FirstOrDefault(i => i.Id == indexerId);
        if (indexer is null)
            return new IndexerCapabilitiesDto { Message = "That indexer no longer exists — reload the panel." };
        if (!_byKey.TryGetValue(indexer.Implementation, out var impl))
            return new IndexerCapabilitiesDto { Message = $"No implementation is registered for \"{indexer.Implementation}\"." };

        // The rate limiter still applies: a Test is a real request to someone else's server, and an admin
        // clicking it repeatedly must not be the thing that gets the tunnel's IP blocked.
        await rateLimiter.WaitAsync(indexer, ct);
        try
        {
            return await impl.TestAsync(indexer, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Capabilities probe threw for indexer \"{Name}\"", indexer.Name);
            return new IndexerCapabilitiesDto { Reachable = false, Message = ex.Message };
        }
    }

    private async Task<(IReadOnlyList<ReleaseCandidate>, ProviderSearchOutcome)> SearchOneAsync(
        IndexerConfigDto indexer, FulfillmentJobDto job, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var cacheKey = $"idx:{indexer.Id}:{CacheKeyFor(job)}";
        if (indexer.CacheSeconds > 0 && cache.TryGetValue<IReadOnlyList<ReleaseCandidate>>(cacheKey, out var cached) && cached is not null)
        {
            logger.LogDebug("{Indexer}: {Count} cached candidate(s) for \"{Title}\"", indexer.Name, cached.Count, job.Title);
            return (cached, new ProviderSearchOutcome(indexer.Id, indexer.Name, true, cached.Count, null, 0));
        }

        try
        {
            await rateLimiter.WaitAsync(indexer, ct);
            var found = await _byKey[indexer.Implementation].SearchAsync(indexer, job, ct);

            // Age filter, when the indexer reports publish dates and the admin set a limit.
            if (indexer.MaxAgeDays is int maxAge && maxAge > 0)
                found = found.Where(c => c.AgeDays is not double age || age <= maxAge).ToList();

            sw.Stop();
            if (indexer.CacheSeconds > 0)
                cache.Set(cacheKey, found, TimeSpan.FromSeconds(indexer.CacheSeconds));

            logger.LogInformation("{Indexer}: {Count} candidate(s) for \"{Title}\" in {Ms}ms",
                indexer.Name, found.Count, job.Title, sw.ElapsedMilliseconds);
            return (found, new ProviderSearchOutcome(indexer.Id, indexer.Name, true, found.Count, null, (int)sw.ElapsedMilliseconds));
        }
        catch (IndexerBlockedException blocked)
        {
            // Blocked is its own outcome, not a generic failure and emphatically not an empty result. The
            // reason travels with it so the panel can say "Blocked by Cloudflare" and offer the remedy that
            // actually applies, rather than showing a healthy indexer that mysteriously finds nothing.
            sw.Stop();
            logger.LogWarning("Indexer {Indexer} blocked ({Reason}) for \"{Title}\"", indexer.Name, blocked.Reason, job.Title);
            return (Array.Empty<ReleaseCandidate>(),
                    new ProviderSearchOutcome(indexer.Id, indexer.Name, false, 0, blocked.Message,
                        (int)sw.ElapsedMilliseconds, blocked.Reason));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            logger.LogWarning(ex, "Indexer {Indexer} failed for \"{Title}\"", indexer.Name, job.Title);
            return (Array.Empty<ReleaseCandidate>(),
                    new ProviderSearchOutcome(indexer.Id, indexer.Name, false, 0, ex.Message, (int)sw.ElapsedMilliseconds));
        }
    }

    /// <summary>
    /// Collapse the same release found on several indexers into one candidate. Without this the ranker saw
    /// (and the admin panel counted) the same torrent repeatedly, and whichever copy happened to be scored
    /// first won. Keeps the highest-priority indexer's attribution while merging the best facts available:
    /// the highest reported seeder count and the first IMDb id anyone supplied.
    /// </summary>
    private IReadOnlyList<ReleaseCandidate> Deduplicate(IEnumerable<ReleaseCandidate> all)
    {
        var byHash = new Dictionary<string, ReleaseCandidate>(StringComparer.OrdinalIgnoreCase);
        var noHash = new List<ReleaseCandidate>();

        foreach (var c in all)
        {
            if (string.IsNullOrWhiteSpace(c.Acquisition.SourceId)) { noHash.Add(c); continue; }
            var resourceKey = AcquisitionResource.BlocklistKey(c.Acquisition.Protocol, c.Acquisition.SourceId);
            if (!byHash.TryGetValue(resourceKey, out var existing)) { byHash[resourceKey] = c; continue; }

            var keep = settings.PriorityOf(c.IndexerId) < settings.PriorityOf(existing.IndexerId) ? c : existing;
            byHash[resourceKey] = keep with
            {
                Seeders = Math.Max(c.Seeders, existing.Seeders),
                SeedersKnown = c.SeedersKnown || existing.SeedersKnown,
                SizeBytes = Math.Max(c.SizeBytes, existing.SizeBytes),
                SizeKnown = c.SizeKnown || existing.SizeKnown,
                ImdbId = keep.ImdbId ?? c.ImdbId ?? existing.ImdbId,
                QualityLabel = keep.QualityLabel ?? c.QualityLabel ?? existing.QualityLabel,
                CategoryIds = c.CategoryIds.Concat(existing.CategoryIds).Distinct().ToList()
            };
        }

        var merged = byHash.Values.Concat(noHash).ToList();
        var duplicates = all.Count() - merged.Count;
        if (duplicates > 0) logger.LogDebug("Merged {Count} duplicate release(s) across indexers", duplicates);
        return merged;
    }

    private async Task ReportHealthAsync(List<ProviderSearchOutcome> outcomes, DateTime searchedAt, CancellationToken ct)
    {
        // Best-effort: telemetry must never fail or delay an actual search.
        try
        {
            await api.ReportIndexerStatusAsync(outcomes.Where(o => o.IndexerId > 0).Select(o => new IndexerStatusReportDto
            {
                IndexerId = o.IndexerId,
                Name = o.Provider,
                Success = o.Success,
                ResultCount = o.Count,
                Error = o.Error,
                LatencyMs = o.LatencyMs,
                SearchedAt = searchedAt,
                BlockReason = o.BlockReason
            }).ToList(), ct);
        }
        catch (Exception ex) { logger.LogDebug(ex, "Indexer status report skipped"); }
    }

    private async Task WriteThroughAsync(
        IEnumerable<ReleaseCandidate> candidates,
        MediaType mediaType,
        DateTime observedAt,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            foreach (var source in candidates.GroupBy(x => new { x.IndexerId, x.Source }))
            {
                var items = source.Select(candidate =>
                {
                    var infoHash = MagnetUtil.Normalize(candidate.Acquisition.SourceId)
                                   ?? MagnetUtil.InfoHashFromMagnet(candidate.Acquisition.Locator);
                    var externalId = infoHash ?? Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Acquisition.Locator)));
                    return new CatalogItemDto
                    {
                        ExternalId = externalId,
                        ReleaseName = candidate.ReleaseName,
                        InfoHash = infoHash,
                        MagnetUri = candidate.Acquisition.Locator,
                        ImdbId = candidate.ImdbId,
                        Seeders = candidate.SeedersKnown ? candidate.Seeders : null,
                        Leechers = candidate.SeedersKnown ? candidate.Leechers : null,
                        SizeBytes = candidate.SizeKnown ? candidate.SizeBytes : null,
                        PublishedAt = candidate.PublishDate,
                        MediaType = mediaType
                    };
                }).GroupBy(x => x.ExternalId, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
                if (items.Count == 0) continue;

                foreach (var chunk in items.Chunk(Math.Clamp(catalogOptions.Value.MaxBatchSize, 1, 1000)))
                {
                    await api.PushCatalogBatchAsync(new CatalogBatchDto
                    {
                        IndexerId = source.Key.IndexerId,
                        Source = source.Key.Source,
                        BatchId = Guid.NewGuid().ToString("N"),
                        AdvanceCheckpoint = false,
                        ObservedAt = observedAt,
                        Items = chunk.ToList()
                    }, budget.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Catalog write-through exceeded its three-second budget");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Catalog write-through is enrichment; it must never change the live search outcome.
            logger.LogDebug(ex, "Catalog write-through skipped");
        }
    }

    /// <summary>Identity of a search for caching: same title/season/episode scope = same query.</summary>
    private static string CacheKeyFor(FulfillmentJobDto job)
    {
        var seasons = string.Join('-', job.RequestedSeasons.OrderBy(s => s));
        var episodes = string.Join('-', job.RequestedEpisodes.Select(e => $"{e.Season}x{e.Episode}").OrderBy(s => s));
        var targets = string.Join('-', job.SeasonTargets.Select(t => t.Season).OrderBy(s => s));
        var identity = job.Media is { IsValid: true } media ? media.StableKey : $"legacy:{job.MediaId}";
        var music = job.Music is null ? string.Empty
            : $"{job.RequestScope}:{job.Music.Artist}:{job.Music.Album}:{job.Music.Track}";
        return $"{identity}:{job.Title}:{job.Year}:{job.ImdbId}:{seasons}:{episodes}:{targets}:{music}";
    }
}
