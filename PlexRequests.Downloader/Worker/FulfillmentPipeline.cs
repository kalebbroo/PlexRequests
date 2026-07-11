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
    Task ResumeAsync(FulfillmentJobDto job, string torrentId, CancellationToken ct);
}

/// <summary>
/// End-to-end processing for a single job: search → rank → add to Deluge → monitor → import →
/// callback. Every terminal outcome reports back to the web app (fulfilled or failed) so a request
/// never silently stalls.
/// </summary>
public class FulfillmentPipeline(
    IIndexerClient indexer,
    IReleaseRanker ranker,
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

            var candidates = await indexer.SearchAsync(job, ct);
            var best = ranker.PickBest(candidates, job);
            if (best is null)
            {
                await api.MarkFailedAsync(job.MediaRequestId, "No acceptable release found", ct);
                return;
            }

            var label = job.MediaType == MediaType.Movie ? deluge.Value.MovieLabel : deluge.Value.TvLabel;
            var torrentId = await downloadClient.AddMagnetAsync(best.Magnet, label, ct);
            if (string.IsNullOrWhiteSpace(torrentId))
            {
                await api.MarkFailedAsync(job.MediaRequestId, "Failed to add torrent to Deluge", ct);
                return;
            }

            await stateStore.SaveAsync(new ActiveJobRecord(job, torrentId), ct);
            await api.ReportProgressAsync(job.Id, 0, ct);
            await MonitorAndImportAsync(job, torrentId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pipeline error for job {JobId}", job.Id);
            await SafeFail(job.MediaRequestId, $"Downloader error: {ex.Message}");
            await SafeRemoveState(job.Id);
        }
    }

    public async Task ResumeAsync(FulfillmentJobDto job, string torrentId, CancellationToken ct)
    {
        try
        {
            logger.LogInformation("Resuming job {JobId} (torrent {TorrentId})", job.Id, torrentId);
            await MonitorAndImportAsync(job, torrentId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resume error for job {JobId}", job.Id);
            await SafeFail(job.MediaRequestId, $"Downloader error on resume: {ex.Message}");
            await SafeRemoveState(job.Id);
        }
    }

    private async Task MonitorAndImportAsync(FulfillmentJobDto job, string torrentId, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, worker.Value.MonitorIntervalSeconds));
        DownloadStatus? status;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            status = await downloadClient.GetStatusAsync(torrentId, ct);
            if (status is null)
            {
                await api.MarkFailedAsync(job.MediaRequestId, "Torrent disappeared from the download client", ct);
                await SafeRemoveState(job.Id);
                return;
            }
            if (string.Equals(status.State, "Error", StringComparison.OrdinalIgnoreCase))
            {
                await api.MarkFailedAsync(job.MediaRequestId, "Torrent entered an error state", ct);
                await SafeRemoveState(job.Id);
                return;
            }

            await api.ReportProgressAsync(job.Id, (int)Math.Round(status.Progress), ct);
            if (status.IsFinished || status.Progress >= 100) break;

            await Task.Delay(interval, ct);
        }

        var sourcePath = Path.Combine(status.SavePath ?? string.Empty, status.Name);
        var imported = await importer.ImportAsync(job, sourcePath, ct);
        if (imported)
        {
            await api.MarkFulfilledAsync(job.MediaRequestId, ct);
            try { await downloadClient.RemoveAsync(torrentId, removeData: false, ct); } // keep files for seeding
            catch (Exception ex) { logger.LogDebug(ex, "Torrent removal after import skipped"); }
        }
        else
        {
            await api.MarkFailedAsync(job.MediaRequestId, "Download completed but import failed", ct);
        }
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
