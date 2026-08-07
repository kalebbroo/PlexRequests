using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Api;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Download;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Worker;

/// <summary>
/// Continuously reconciles what the database believes is downloading against what the download client is
/// actually doing, and drives every state change from that comparison.
///
/// This replaces per-job, in-process monitoring, which had a structural flaw rather than a bug: a job's
/// torrents were watched by a loop belonging to the process that started them, so if that process was
/// replaced — a deploy, a crash, an OOM — the torrents kept downloading with nobody watching. Nothing ever
/// asked "does the client agree with the database?", so drift was permanent and invisible. The live
/// symptom was a job marked "Deferred — no acceptable release found yet" while sixty-seven torrents it had
/// added sat in Deluge, nine of them finished, none imported, and the job re-searching forever.
///
/// The loop is stateless and idempotent. It holds nothing between passes; every cycle re-derives the world
/// from the client plus the database. A restart at any moment is therefore a no-op, which is the property
/// that makes it hard to break — there is no in-memory state left to lose.
/// </summary>
public class TorrentReconciler(
    IPlexRequestsApiClient api,
    IDownloadClient downloadClient,
    IOptions<WorkerOptions> workerOptions,
    ILogger<TorrentReconciler> logger) : BackgroundService
{
    private readonly TimeSpan _interval =
        TimeSpan.FromSeconds(Math.Clamp(workerOptions.Value.MonitorIntervalSeconds, 10, 120));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the web app finish its migrations before the first pass.
        try { await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken); }
        catch (OperationCanceledException) { return; }

        logger.LogInformation("Torrent reconciler started (every {Seconds}s)", _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Reconciliation pass failed; retrying next cycle"); }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task<int> ReconcileOnceAsync(CancellationToken ct)
    {
        var tracked = await api.GetActiveTorrentsAsync(ct);
        if (tracked.Count == 0) return 0;

        var updates = new List<TorrentStateUpdateDto>();
        var now = DateTime.UtcNow;

        foreach (var t in tracked)
        {
            if (ct.IsCancellationRequested) break;

            // One status call per tracked torrent. Deluge exposes a bulk form, but the per-torrent call is
            // what the existing client already speaks and the set here is bounded by what we ourselves
            // added — not by everything in the client, which may include the operator's own torrents.
            DownloadStatus? status = null;
            try { status = await downloadClient.GetStatusAsync(t.TorrentId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed status call is not evidence the torrent is gone — the client may simply be
                // unreachable. Skipping leaves the row untouched for the next pass, which is the safe
                // reading: never declare a download dead because we could not ask about it.
                logger.LogDebug(ex, "Status unavailable for {Torrent}; leaving it for the next pass", t.TorrentId);
                continue;
            }

            var obs = Observe(status);
            var decision = TorrentHealth.Decide(obs, t.AddedAt, t.ProgressChangedAt, now);

            var update = new TorrentStateUpdateDto
            {
                TorrentId = t.TorrentId,
                Progress = obs.Progress,
                Seeds = obs.Seeds,
                Peers = obs.Peers,
                DownloadRateBytesPerSec = status?.DownloadRate ?? 0,
                TotalSizeBytes = status?.TotalSizeBytes ?? 0,
                TrackerStatus = obs.TrackerStatus,
                Reason = decision.Reason,
                State = decision.Verdict switch
                {
                    TorrentVerdict.Import => TorrentTrackingState.Finished,
                    TorrentVerdict.Fail => TorrentTrackingState.Failed,
                    TorrentVerdict.Gone => TorrentTrackingState.Missing,
                    _ => TorrentTrackingState.Active
                }
            };
            updates.Add(update);

            if (decision.Verdict == TorrentVerdict.Fail && decision.Blocklist)
                await SafeBlocklist(t, decision.Reason, obs.ClientState.Equals("error", StringComparison.OrdinalIgnoreCase), ct);
        }

        if (updates.Count == 0) return 0;
        await api.ReportTorrentStateAsync(updates, ct);

        var finished = updates.Count(u => u.State == TorrentTrackingState.Finished);
        var failed = updates.Count(u => u.State == TorrentTrackingState.Failed);
        var gone = updates.Count(u => u.State == TorrentTrackingState.Missing);
        if (finished + failed + gone > 0)
            logger.LogInformation("Reconciled {Total} torrent(s): {Finished} finished, {Failed} failed, {Gone} missing",
                updates.Count, finished, failed, gone);

        return updates.Count;
    }

    /// <summary>
    /// Translate the client's status into the observation the pure rules consume. A null status means the
    /// client answered and does not have this torrent — genuinely gone, as opposed to the exception path
    /// above where we could not ask at all. The two must not be conflated: one is a fact, the other is
    /// ignorance, and only the fact justifies giving up.
    /// </summary>
    private static TorrentObservation Observe(DownloadStatus? status)
    {
        if (status is null) return new TorrentObservation { PresentInClient = false };

        return new TorrentObservation
        {
            PresentInClient = true,
            ClientState = status.State ?? string.Empty,
            Progress = status.Progress,
            Seeds = status.Seeds,
            Peers = status.Peers,
            IsFinished = status.IsFinished,
            TrackerStatus = status.TrackerStatus,
            // A magnet has no size until its metadata resolves; that is how we tell "still resolving"
            // apart from "downloading nothing".
            HasMetadata = status.TotalSizeBytes > 0
        };
    }

    private async Task SafeBlocklist(TrackedTorrentDto t, string? reason, bool clientError, CancellationToken ct)
    {
        // Blocklisting is what stops the job picking the same dead release on its next search. Best-effort:
        // failing to record it must not prevent the torrent being marked failed, or the job would keep
        // waiting on something already given up on.
        try
        {
            await api.BlocklistAsync(t.FulfillmentJobId, new BlocklistRequestDto
            {
                InfoHash = t.InfoHash,
                ReleaseName = t.ReleaseName ?? string.Empty,
                // Stalled covers both shapes the reconciler gives up on — no progress with no peers, and a
                // magnet whose metadata never arrived. TorrentError is reserved for the client saying so.
                Reason = clientError ? BlocklistReason.TorrentError : BlocklistReason.Stalled,
                Detail = reason,
                Season = t.Season,
                Episode = t.Episode
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not blocklist {Torrent}", t.TorrentId);
        }
    }
}
