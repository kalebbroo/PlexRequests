using Microsoft.Extensions.Options;
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
        Interlocked.Increment(ref _active);
        _ = Task.Run(async () =>
        {
            try { await work(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.LogError(ex, "Job {JobId} crashed", job.Id); }
            finally { Interlocked.Decrement(ref _active); }
        });
    }
}
