using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Api;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Worker;

/// <summary>
/// Migrates Torznab endpoints out of this container's environment and into the database, once.
///
/// It has to happen here rather than in the web app: <c>Indexer__Torznab__0__Url</c> and friends are set on
/// the DOWNLOADER (see docker-compose.yml), and the web container has no way to read another container's
/// environment. Without this the admin would have to re-enter a URL and API key that were already working.
///
/// Guarded by a marker file beside the job-state file so a restart can't re-import endpoints the admin has
/// since deleted on purpose; the server side is also idempotent by URL, so a lost marker is harmless.
/// </summary>
public class LegacyTorznabImporter(
    IPlexRequestsApiClient api,
    IOptions<IndexerOptions> indexerOptions,
    IOptions<WorkerOptions> workerOptions,
    ILogger<LegacyTorznabImporter> logger) : IHostedService
{
    private readonly IndexerOptions _indexer = indexerOptions.Value;
    private readonly WorkerOptions _worker = workerOptions.Value;

    public async Task StartAsync(CancellationToken ct)
    {
        try { await RunAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never block worker startup on a best-effort migration.
            logger.LogWarning(ex, "Legacy Torznab import skipped");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task RunAsync(CancellationToken ct)
    {
        var configured = _indexer.Torznab
            .Where(e => !string.IsNullOrWhiteSpace(e.Url))
            .ToList();
        if (configured.Count == 0) return;

        var marker = MarkerPath();
        if (File.Exists(marker)) return;

        var payload = configured.Select(e => new LegacyTorznabEndpointDto
        {
            Name = string.IsNullOrWhiteSpace(e.Name) ? "Torznab" : e.Name,
            Url = e.Url,
            ApiKey = e.ApiKey,
            Enabled = e.Enabled
        }).ToList();

        var imported = await api.ImportLegacyTorznabAsync(payload, ct);

        // Only claim completion once the server has actually accepted them, so a web app that was down at
        // startup doesn't cause the endpoints to be lost.
        if (imported >= 0)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                await File.WriteAllTextAsync(marker, DateTime.UtcNow.ToString("O"), ct);
            }
            catch (Exception ex) { logger.LogDebug(ex, "Could not write the Torznab import marker"); }
        }

        if (imported > 0)
            logger.LogWarning("Imported {Count} Torznab endpoint(s) from this worker's configuration into the admin Indexers panel. " +
                              "They are now managed there — remove the Indexer__Torznab__* environment variables from your compose file.",
                imported);
        else
            logger.LogInformation("Torznab endpoints from configuration were already present in the admin Indexers panel");
    }

    private string MarkerPath()
    {
        var dir = Path.GetDirectoryName(_worker.StatePath);
        return Path.Combine(string.IsNullOrWhiteSpace(dir) ? "state" : dir, "torznab-import.done");
    }
}
