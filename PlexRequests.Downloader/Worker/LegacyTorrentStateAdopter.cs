using PlexRequests.Downloader.Api;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Worker;

/// <summary>
/// Bridges the worker's old local job state into the durable torrent table, once per start.
///
/// Without this the reconciler is born blind. It only knows about torrents registered through the new
/// path, so every download already in flight when it shipped — the very ones it was written to rescue —
/// stays invisible. On the live deployment that was seventy torrents across four jobs, nine of them
/// finished on disk and unimported, with the tracking table sitting at zero rows after deploy.
///
/// It reads what the previous design persisted (<c>active-jobs.json</c>) and hands it to the API, which
/// treats registration as idempotent. That makes running this on every start safe and self-limiting: the
/// local file shrinks as jobs complete, so the work naturally goes to nothing rather than needing a marker
/// file to remember it has run — one less piece of state to get wrong.
/// </summary>
public class LegacyTorrentStateAdopter(
    IJobStateStore stateStore,
    IPlexRequestsApiClient api,
    ILogger<LegacyTorrentStateAdopter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // After the web app's migrations, before the reconciler's first pass has anything to do.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            var records = await stateStore.GetAllAsync(stoppingToken);
            if (records.Count == 0) return;

            var adopted = 0;
            foreach (var record in records)
            {
                if (stoppingToken.IsCancellationRequested) break;

                // Torrents already marked imported in the old state are done; re-registering them would
                // invite the reconciler to import them a second time.
                var pending = record.Transfers.Where(t => !t.Imported && !string.IsNullOrWhiteSpace(t.TransferId)).ToList();
                if (pending.Count == 0) continue;

                var ok = await api.RegisterTransfersAsync(record.Job.Id, pending.Select(t => new TrackedTransferDto
                {
                    FulfillmentJobId = record.Job.Id,
                    TransferId = t.TransferId,
                    Protocol = t.Protocol,
                    // The old record kept only the client's id. For a v1 torrent that IS the infohash, which
                    // is what the blocklist needs; for anything else it stays null rather than being guessed.
                    SourceId = t.SourceId ?? MagnetUtil.Normalize(t.TransferId),
                    ReleaseName = t.ReleaseName,
                    Source = t.Source,
                    IndexerId = t.IndexerId,
                    Season = t.Season,
                    Episode = t.Episode,
                    IsPack = t.IsPack,
                    NeededEpisodes = t.NeededEpisodes?.ToList() ?? new(),
                    NeededEpisodeRefs = t.NeededEpisodeRefs?.ToList() ?? new(),
                    Resolution = t.Resolution
                }).ToList(), stoppingToken);

                if (ok) adopted += pending.Count;
                else logger.LogWarning("Could not adopt {Count} torrent(s) for job {JobId}; will retry next start",
                    pending.Count, record.Job.Id);
            }

            if (adopted > 0)
                logger.LogInformation("Adopted {Count} in-flight torrent(s) from local state into durable tracking", adopted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never block startup over this: the reconciler still works for everything registered normally.
            logger.LogWarning(ex, "Legacy torrent-state adoption skipped");
        }
    }
}
