using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Api;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Download;
using PlexRequests.Downloader.Import;
using PlexRequests.Downloader.Indexers;
using PlexRequests.Downloader.Ranking;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Worker;

public interface IFulfillmentPipeline
{
    Task ProcessAsync(FulfillmentJobDto job, CancellationToken ct);
    Task ResumeAsync(ActiveJobRecord record, CancellationToken ct);
}

/// <summary>
/// End-to-end processing for a single job: search → plan → add to Deluge → monitor → import → callback.
/// A plan may be one release (movie / season pack) or several (season packs, or individual episodes when
/// no acceptable pack exists); every torrent is tracked and the request is only fulfilled once all import.
/// Every terminal outcome reports back to the web app so a request never silently stalls.
/// </summary>
public class FulfillmentPipeline(
    IIndexerClient indexer,
    IReleaseRanker ranker,
    IDownloadPreferencesProvider prefs,
    IDownloadClient downloadClient,
    ILibraryImporter importer,
    IPlexRequestsApiClient api,
    IJobStateStore stateStore,
    IOptions<DelugeOptions> deluge,
    IOptions<WorkerOptions> worker,
    ILogger<FulfillmentPipeline> logger) : IFulfillmentPipeline
{
    public async Task ProcessAsync(FulfillmentJobDto job, CancellationToken ct)
    {
        try
        {
            logger.LogInformation("Processing job {JobId}: \"{Title}\" [{Type}]", job.Id, job.Title, job.MediaType);

            await prefs.RefreshAsync(ct); // pick up the latest admin config before ranking

            var candidates = await indexer.SearchAsync(job, ct);
            var plan = ranker.PlanDownload(candidates, job);
            if (plan.IsEmpty)
            {
                await api.MarkFailedAsync(job.MediaRequestId, "No acceptable release found", ct);
                return;
            }

            var label = job.MediaType == MediaType.Movie ? deluge.Value.MovieLabel : deluge.Value.TvLabel;
            var torrents = new List<TorrentItem>();
            foreach (var item in plan.Items)
            {
                var torrentId = await downloadClient.AddMagnetAsync(item.Candidate.Magnet, label, ct);
                if (string.IsNullOrWhiteSpace(torrentId))
                {
                    logger.LogWarning("Failed to add magnet for job {JobId} (S{Season}E{Episode})", job.Id, item.Season, item.Episode);
                    continue;
                }
                torrents.Add(new TorrentItem(torrentId, item.Season, item.Episode, item.IsPack));
            }

            if (torrents.Count == 0)
            {
                await api.MarkFailedAsync(job.MediaRequestId, "Failed to add torrent(s) to Deluge", ct);
                return;
            }

            logger.LogInformation("Job {JobId} \"{Title}\": added {Count} torrent(s) [{Kind}]", job.Id, job.Title, torrents.Count, plan.Kind);
            var record = new ActiveJobRecord(job, torrents);
            await stateStore.SaveAsync(record, ct);
            await api.ReportProgressAsync(job.Id, 0, ct);
            await MonitorAndImportAllAsync(record, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pipeline error for job {JobId}", job.Id);
            await SafeFail(job.MediaRequestId, $"Downloader error: {ex.Message}");
            await SafeRemoveState(job.Id);
        }
    }

    public async Task ResumeAsync(ActiveJobRecord record, CancellationToken ct)
    {
        try
        {
            logger.LogInformation("Resuming job {JobId} ({Count} torrent(s))", record.Job.Id, record.Torrents.Count);
            await MonitorAndImportAllAsync(record, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resume error for job {JobId}", record.Job.Id);
            await SafeFail(record.Job.MediaRequestId, $"Downloader error on resume: {ex.Message}");
            await SafeRemoveState(record.Job.Id);
        }
    }

    /// <summary>
    /// Poll every torrent backing the job; import each as it finishes (so partial successes persist to Plex),
    /// report aggregate progress, and mark the request fulfilled only once all torrents import. If any torrent
    /// errors/disappears or fails to import, the request is failed — a retry recomputes only the still-missing
    /// episodes, so already-imported ones aren't refetched.
    /// </summary>
    private async Task MonitorAndImportAllAsync(ActiveJobRecord record, CancellationToken ct)
    {
        var job = record.Job;
        var items = record.Torrents.ToList(); // working copy; entries replaced as they import
        var interval = TimeSpan.FromSeconds(Math.Max(5, worker.Value.MonitorIntervalSeconds));

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            double progressSum = 0;
            string? failReason = null;

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.Imported) { progressSum += 100; continue; }

                var status = await downloadClient.GetStatusAsync(it.TorrentId, ct);
                if (status is null) { failReason = "A torrent disappeared from the download client"; break; }
                if (string.Equals(status.State, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    failReason = "A torrent entered an error state"; break;
                }

                progressSum += status.Progress;

                if (status.IsFinished || status.Progress >= 100)
                {
                    var sourcePath = Path.Combine(status.SavePath ?? string.Empty, status.Name);
                    var imported = await importer.ImportAsync(job, sourcePath, ct);
                    if (!imported) { failReason = "A download completed but import failed"; break; }

                    items[i] = it with { Imported = true };
                    await stateStore.SaveAsync(record with { Torrents = items.ToList() }, ct); // persist so a restart resumes
                    try { await downloadClient.RemoveAsync(it.TorrentId, removeData: false, ct); } // keep files for seeding
                    catch (Exception ex) { logger.LogDebug(ex, "Torrent removal after import skipped"); }
                }
            }

            await api.ReportProgressAsync(job.Id, (int)Math.Round(progressSum / Math.Max(1, items.Count)), ct);

            if (failReason is not null)
            {
                await api.MarkFailedAsync(job.MediaRequestId, failReason, ct);
                await SafeRemoveState(job.Id);
                return;
            }

            if (items.All(x => x.Imported)) break;

            await Task.Delay(interval, ct);
        }

        await api.MarkFulfilledAsync(job.MediaRequestId, ct);
        await SafeRemoveState(job.Id);
    }

    private async Task SafeFail(int requestId, string reason)
    {
        try { await api.MarkFailedAsync(requestId, reason, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not report failure for request {RequestId}", requestId); }
    }

    private async Task SafeRemoveState(int jobId)
    {
        try { await stateStore.RemoveAsync(jobId, CancellationToken.None); }
        catch (Exception ex) { logger.LogDebug(ex, "State cleanup skipped for job {JobId}", jobId); }
    }
}
