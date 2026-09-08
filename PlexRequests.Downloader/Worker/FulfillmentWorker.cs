using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using PlexRequests.Downloader.Api;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Vpn;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Worker;

/// <summary>
/// Orchestrates the loop: resume in-flight jobs on startup, then on each tick verify the VPN is up,
/// claim as many new jobs as there is free concurrency for, and process each on its own task.
/// </summary>
public class FulfillmentWorker(
    IPlexRequestsApiClient api,
    IFulfillmentPipeline pipeline,
    IJobStateStore stateStore,
    IVpnGuard vpn,
    IOptions<WorkerOptions> options,
    ILogger<FulfillmentWorker> logger) : BackgroundService
{
    private readonly WorkerOptions _opts = options.Value;
    private int _active;
    private readonly ConcurrentDictionary<int, byte> _runningJobs = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Fulfillment worker '{WorkerId}' started (max {Max} concurrent, poll {Poll}s)",
            _opts.WorkerId, _opts.MaxConcurrent, _opts.PollIntervalSeconds);

        // Resume anything that was in-flight when we last stopped.
        try
        {
            foreach (var rec in await stateStore.GetAllAsync(stoppingToken))
            {
                logger.LogInformation("Resuming in-flight job {JobId} \"{Title}\"", rec.Job.Id, rec.Job.Title);
                StartTracked(rec.Job, () => pipeline.ResumeAsync(rec, stoppingToken));
            }
        }
        catch (Exception ex) { logger.LogError(ex, "Failed to resume persisted jobs"); }

        var interval = TimeSpan.FromSeconds(Math.Max(5, _opts.PollIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await vpn.IsHealthyAsync(stoppingToken))
                {
                    // VPN down ⇒ no egress; hold off claiming/downloading until it recovers.
                }
                else
                {
                    await RecoverDurableJobsAsync(stoppingToken);
                    var free = _opts.MaxConcurrent - Volatile.Read(ref _active);
                    if (free > 0)
                    {
                        var jobs = await api.ClaimAsync(Math.Min(free, _opts.ClaimBatchSize), stoppingToken);
                        foreach (var job in jobs)
                            StartTracked(job, () => pipeline.ProcessAsync(job, stoppingToken));
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Poll loop iteration failed"); }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        logger.LogInformation("Fulfillment worker stopping");
    }

    private void StartTracked(FulfillmentJobDto job, Func<Task> work)
    {
        if (!_runningJobs.TryAdd(job.Id, 0)) return;
        Interlocked.Increment(ref _active);
        _ = Task.Run(async () =>
        {
            try { await work(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.LogError(ex, "Job {JobId} crashed", job.Id); }
            finally
            {
                _runningJobs.TryRemove(job.Id, out _);
                Interlocked.Decrement(ref _active);
            }
        });
    }

    /// <summary>
    /// Rebuild a missing/corrupt local monitor record from the database's durable transfer intent. The
    /// stateless reconciler can place a finished file, but the per-job monitor owns aggregate completion,
    /// partial continuation, and request notifications; both halves must survive losing active-jobs.json.
    /// This runs every poll because the web app may still be starting during the worker's first pass.
    /// </summary>
    private async Task RecoverDurableJobsAsync(CancellationToken ct)
    {
        var durable = await api.GetActiveTransfersAsync(ct);
        foreach (var group in durable.GroupBy(transfer => transfer.FulfillmentJobId))
        {
            if (_runningJobs.ContainsKey(group.Key)) continue;
            var job = await api.GetJobAsync(group.Key, ct);
            if (job is null || _runningJobs.ContainsKey(group.Key)) continue;

            var record = BuildDurableRecoveryRecord(job, group.ToList());
            await stateStore.SaveAsync(record, ct);
            logger.LogInformation(
                "Recovered in-flight job {JobId} \"{Title}\" from {Count} durable transfer(s)",
                job.Id, job.Title, record.Transfers.Count);
            StartTracked(job, () => pipeline.ResumeAsync(record, ct));
        }
    }

    internal static ActiveJobRecord BuildDurableRecoveryRecord(FulfillmentJobDto job,
        IReadOnlyCollection<TrackedTransferDto> durable)
    {
        var transfers = durable.Select(transfer => new TransferItem(
            transfer.TransferId,
            transfer.Season,
            transfer.Episode,
            transfer.IsPack,
            Imported: false,
            NeededEpisodes: transfer.NeededEpisodes,
            Resolution: transfer.Resolution,
            Source: transfer.Source,
            IndexerId: transfer.IndexerId,
            ReleaseName: transfer.ReleaseName,
            Protocol: transfer.Protocol,
            SourceId: transfer.SourceId,
            NeededEpisodeRefs: transfer.NeededEpisodeRefs,
            SourceSeason: transfer.SourceSeason,
            FractionalEpisodeInsertionAfter: transfer.FractionalEpisodeInsertionAfter)).ToList();

        var wanted = job.RequestedEpisodes
            .Concat(job.SeasonTargets.SelectMany(target => target.MissingEpisodes.Select(episode =>
                new EpisodeRef { Season = target.Season, Episode = episode })))
            .Where(target => target.Season >= 0 && target.Episode > 0)
            .Select(target => (target.Season, target.Episode)).ToHashSet();
        var covered = transfers.SelectMany(FulfillmentPipeline.CanonicalTargets).ToHashSet();
        var coversAllTargets = wanted.Count == 0 || wanted.IsSubsetOf(covered);
        return new ActiveJobRecord(job, transfers, coversAllTargets);
    }
}
