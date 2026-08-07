using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Api;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Download;
using PlexRequests.Downloader.Import;
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
    ILibraryImporter importer,
    IOptions<WorkerOptions> workerOptions,
    ILogger<TorrentReconciler> logger) : BackgroundService
{
    private readonly TimeSpan _interval =
        TimeSpan.FromSeconds(Math.Clamp(workerOptions.Value.MonitorIntervalSeconds, 10, 120));

    /// <summary>
    /// Results are flushed in batches rather than once at the end of the pass. With seventy torrents and an
    /// unresponsive client, an all-or-nothing pass wrote absolutely nothing for over half an hour — every
    /// status call burning the HttpClient's full timeout and the single report never being reached. Partial
    /// progress is strictly better than none, and the reconciler is idempotent, so a half-finished pass
    /// costs nothing.
    /// </summary>
    private const int FlushEvery = 10;

    /// <summary>
    /// A hard ceiling per status call, well under the HTTP client's own timeout. One wedged connection must
    /// not be able to consume the pass: at the default 30s timeout, seventy torrents meant a 35-minute
    /// cycle that appeared frozen from outside.
    /// </summary>
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(8);

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
        var flushed = 0;
        var now = DateTime.UtcNow;

        foreach (var t in tracked)
        {
            if (ct.IsCancellationRequested) break;

            // One status call per tracked torrent. Deluge exposes a bulk form, but the per-torrent call is
            // what the existing client already speaks and the set here is bounded by what we ourselves
            // added — not by everything in the client, which may include the operator's own torrents.
            DownloadStatus? status = null;
            using var perCall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perCall.CancelAfter(StatusTimeout);
            try { status = await downloadClient.GetStatusAsync(t.TorrentId, perCall.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our own ceiling fired, not shutdown. Treat exactly like an unreachable client: say nothing
                // about this torrent and try again next cycle. Never infer absence from a timeout.
                logger.LogDebug("Status call for {Torrent} exceeded {Seconds}s; skipping this pass",
                    t.TorrentId, StatusTimeout.TotalSeconds);
                continue;
            }
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
            // A finished torrent is imported here and now, by whoever noticed — not only by the process that
            // added it. That is the whole point: nine finished episodes sat unimported for days because the
            // only code that could import them belonged to a worker that had been replaced.
            if (decision.Verdict == TorrentVerdict.Import && status is not null)
            {
                var outcome = await TryImportAsync(t, status, ct);
                update.State = outcome.State;
                update.Reason = outcome.Reason ?? update.Reason;
            }

            updates.Add(update);

            if (update.State == TorrentTrackingState.Failed && decision.Blocklist)
                await SafeBlocklist(t, update.Reason, obs.ClientState.Equals("error", StringComparison.OrdinalIgnoreCase), ct);

            if (updates.Count - flushed >= FlushEvery)
            {
                await api.ReportTorrentStateAsync(updates.Skip(flushed).ToList(), ct);
                flushed = updates.Count;
            }
        }

        if (updates.Count == 0) return 0;
        if (flushed < updates.Count) await api.ReportTorrentStateAsync(updates.Skip(flushed).ToList(), ct);

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

    /// <summary>
    /// Move a finished torrent's files into the library.
    ///
    /// Two failure shapes are kept apart. A path that will not resolve yet is NOT a failure: Deluge reports
    /// is_finished before the data is necessarily flushed, so the row simply stays Finished and the next
    /// pass tries again — the reconciler runs every cycle, so waiting costs nothing and needs no timer.
    /// An import that genuinely fails IS terminal, and blocklists, because retrying it forever would peg
    /// the disk and never succeed.
    /// </summary>
    private async Task<(TorrentTrackingState State, string? Reason)> TryImportAsync(
        TrackedTorrentDto t, DownloadStatus status, CancellationToken ct)
    {
        var job = await api.GetJobAsync(t.FulfillmentJobId, ct);
        if (job is null)
            return (TorrentTrackingState.Finished, "Waiting: the job could not be read");

        var sourcePath = ImportSourceResolver.Resolve(status, t.FulfillmentJobId, t.TorrentId, logger);
        if (sourcePath is null)
            return (TorrentTrackingState.Finished, "Waiting: files not on disk yet");

        var item = new TorrentItem(t.TorrentId, t.Season, t.Episode, t.IsPack,
            NeededEpisodes: t.NeededEpisodes.Count > 0 ? t.NeededEpisodes : null, Resolution: t.Resolution);

        var result = await importer.ImportAsync(job, item, sourcePath, ct);
        if (!result.Success)
            return (TorrentTrackingState.Failed, result.FailReason ?? "Import failed");

        try
        {
            await api.ReportImportedFilesAsync(job.Id, result.Files.Select(f => new ImportedFileDto
            {
                TorrentId = t.TorrentId,
                SourcePath = f.SourcePath,
                DestinationPath = f.DestinationPath,
                FileType = f.FileType,
                SeasonNumber = f.Season,
                EpisodeNumber = f.Episode,
                SizeBytes = f.SizeBytes,
                ResolutionHeight = t.Resolution
            }).ToList(), ct);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not persist import audit rows for job {JobId}", job.Id); }

        try { await api.RefreshLibraryAsync(job.MediaType, ct); }
        catch (Exception ex) { logger.LogDebug(ex, "Plex refresh trigger skipped"); }

        // Files are kept so the torrent can go on seeding; only the client's entry goes.
        try { await downloadClient.RemoveAsync(t.TorrentId, removeData: false, ct); }
        catch (Exception ex) { logger.LogDebug(ex, "Torrent removal after import skipped"); }

        logger.LogInformation("Imported {Count} file(s) for job {JobId} from {Release}",
            result.Files.Count, job.Id, t.ReleaseName ?? t.TorrentId);
        return (TorrentTrackingState.Imported, null);
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
