using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Api;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Worker;

/// <summary>
/// Polls each incremental source once and writes a durable catalog. Ingestion is independent of whether
/// the separately staged catalog search and monitoring read paths are enabled.
/// </summary>
public sealed class ReleaseIngestionWorker(
    IEnumerable<IReleaseFeedSource> feeds,
    IIndexerSettingsProvider indexerSettings,
    IPlexRequestsApiClient api,
    IOptions<CatalogWorkerOptions> options,
    ILogger<ReleaseIngestionWorker> logger) : BackgroundService
{
    private readonly Dictionary<string, IReleaseFeedSource> _feeds =
        feeds.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
    private readonly CatalogWorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Release catalog ingestion is disabled");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_options.StartDelaySeconds, 0, 3600)), stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        var interval = TimeSpan.FromMinutes(Math.Clamp(_options.PollIntervalMinutes, 1, 1440));
        logger.LogInformation("Release catalog shadow ingestion started (every {Minutes} minutes, {FeedCount} adapter(s))",
            interval.TotalMinutes, _feeds.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await IngestOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Release catalog ingestion pass failed"); }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task IngestOnceAsync(CancellationToken cancellationToken)
    {
        await indexerSettings.RefreshAsync(cancellationToken);
        var sources = indexerSettings.All
            .Where(x => x.Enabled && x.EnableIngestion && _feeds.ContainsKey(x.Implementation))
            .OrderBy(x => x.Priority)
            .ToList();

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var checkpoint = await api.GetCatalogCheckpointAsync(source.Id, cancellationToken);
            if (checkpoint is null)
            {
                logger.LogDebug("Catalog checkpoint unavailable for {Source}; leaving its cursor untouched", source.Name);
                continue;
            }
            if (checkpoint.NextAttemptAt is DateTime next && next > DateTime.UtcNow)
            {
                logger.LogDebug("Catalog circuit for {Source} is open until {NextAttempt}", source.Name, next);
                continue;
            }

            try
            {
                var page = await _feeds[source.Implementation].FetchAsync(
                    source,
                    checkpoint.Cursor,
                    Math.Clamp(_options.MaxBatchSize, 1, 1000),
                    cancellationToken);
                var batch = new CatalogBatchDto
                {
                    IndexerId = source.Id,
                    Source = source.Name,
                    BatchId = Guid.NewGuid().ToString("N"),
                    Cursor = page.NextCursor,
                    ObservedAt = DateTime.UtcNow,
                    Items = page.Items.ToList()
                };
                var committed = await api.PushCatalogBatchAsync(batch, cancellationToken);
                if (committed is null)
                {
                    logger.LogWarning("Catalog batch for {Source} was not acknowledged; it will be discovered again from the committed cursor",
                        source.Name);
                    continue;
                }

                logger.LogInformation(
                    "Catalog {Source}: {Items} item(s), {Releases} new release(s), {Unresolved} awaiting hydration",
                    source.Name,
                    page.Items.Count,
                    committed.ReleasesInserted,
                    committed.UnresolvedSightings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var blockReason = ex is IndexerBlockedException blocked
                    ? blocked.Reason : IndexerBlockReason.None;
                var updated = await api.ReportCatalogFailureAsync(new CatalogFailureDto
                {
                    IndexerId = source.Id,
                    Source = source.Name,
                    Error = ex.Message,
                    BlockReason = blockReason,
                    OccurredAt = DateTime.UtcNow
                }, cancellationToken);
                logger.LogWarning(ex, "Catalog ingestion failed for {Source}; next probe {NextProbe}",
                    source.Name, updated?.NextAttemptAt);
            }
        }
    }
}
