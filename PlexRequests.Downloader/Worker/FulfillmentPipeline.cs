using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Api;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Download;
using PlexRequests.Downloader.Import;
using PlexRequests.Downloader.Indexers;
using PlexRequests.Downloader.Ranking;
using PlexRequests.Downloader.Vpn;
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
    IReleaseParser parser,
    IDownloadPreferencesProvider prefs,
    ILibraryOrganizationProvider libraryPrefs,
    IDownloadClient downloadClient,
    ILibraryImporter importer,
    IPlexRequestsApiClient api,
    IJobStateStore stateStore,
    IVpnGuard vpn,
    IOptions<DelugeOptions> deluge,
    IOptions<WorkerOptions> worker,
    ILogger<FulfillmentPipeline> logger) : IFulfillmentPipeline
{
    public async Task ProcessAsync(FulfillmentJobDto job, CancellationToken ct)
    {
        try
        {
            logger.LogInformation("Processing job {JobId}: \"{Title}\" [{Type}]", job.Id, job.Title, job.MediaType);

            // Touch LastUpdatedAt immediately after claim, before the (potentially slow) search/rank/add
            // phase — closes the split-brain window where the reaper could otherwise requeue this job
            // as "stale" while it's still genuinely being worked on, since the next touch wouldn't
            // normally happen until after torrents are added and the monitor loop starts.
            await SafeReportProgress(job.Id, 0);

            await prefs.RefreshAsync(ct); // pick up the latest admin config before ranking
            await libraryPrefs.RefreshAsync(ct); // and the latest library-organization config before importing

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
                torrents.Add(new TorrentItem(torrentId, item.Season, item.Episode, item.IsPack, NeededEpisodes: item.NeededEpisodes));
            }

            if (torrents.Count == 0)
            {
                await api.MarkFailedAsync(job.MediaRequestId, "Failed to add torrent(s) to Deluge", ct);
                return;
            }

            logger.LogInformation("Job {JobId} \"{Title}\": added {Count} torrent(s) [{Kind}]", job.Id, job.Title, torrents.Count, plan.Kind);
            var record = new ActiveJobRecord(job, torrents);
            await stateStore.SaveAsync(record, ct);
            await SafeReportProgress(job.Id, 0);
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
    /// report aggregate progress, and mark the request fulfilled only once all torrents reach a terminal state.
    /// Failures are isolated PER TORRENT: if one torrent errors/disappears/stalls or fails to import, only that
    /// torrent is dropped (and its partial data removed) — its healthy siblings keep downloading independently
    /// rather than being wiped alongside it. The request is fulfilled if every torrent imported, partially
    /// completed if some imported and some failed, or failed if none imported. A retry recomputes only the
    /// still-missing episodes, so already-imported ones aren't refetched.
    /// </summary>
    private async Task MonitorAndImportAllAsync(ActiveJobRecord record, CancellationToken ct)
    {
        var job = record.Job;
        var items = record.Torrents.ToList(); // working copy; entries replaced as they import
        var interval = TimeSpan.FromSeconds(Math.Max(5, worker.Value.MonitorIntervalSeconds));
        var stallTimeout = TimeSpan.FromMinutes(Math.Max(5, worker.Value.StallTimeoutMinutes));
        var finishSettle = TimeSpan.FromSeconds(Math.Max(5, worker.Value.FinishSettleSeconds));
        // Per-torrent stall tracking: last progress value seen + when it last changed.
        var lastProgress = new Dictionary<string, (double Progress, DateTime ChangedAt)>();
        // Per-torrent finish-settle tracking: when the torrent first reported finished (to grace the
        // is_finished-before-flush race before declaring a path-resolution failure).
        var finishedSince = new Dictionary<string, DateTime>();
        // Torrents that hit a terminal failure — dropped, but do not abort the rest of the batch.
        var failed = new HashSet<string>();
        var failReasons = new List<string>();
        // Season packs restricted to specific episodes get their unwanted files deselected in Deluge once
        // metadata resolves — tracked here so we only apply it once per torrent.
        var trimmed = new HashSet<string>();
        // Latest raw client status per torrent, kept so we can assemble a live telemetry snapshot for the
        // admin downloads panel at any point in the tick (including right before a blocking import).
        var latest = new Dictionary<string, DownloadStatus>();
        var now0 = DateTime.UtcNow;
        foreach (var it in items) lastProgress[it.TorrentId] = (0, now0);

        // Assemble the per-torrent telemetry snapshot pushed up with each progress report. Failed torrents
        // are omitted; a torrent named in importingId is forced to the Importing stage (it's mid-move). This
        // is display-only — never drives control flow — so unknown/missing fields just read as 0.
        List<DownloadTorrentTelemetry> BuildTelemetry(string? importingId = null)
        {
            var list = new List<DownloadTorrentTelemetry>();
            foreach (var it in items)
            {
                if (failed.Contains(it.TorrentId)) continue;
                latest.TryGetValue(it.TorrentId, out var st);
                DownloadTorrentStage stage;
                if (it.Imported) stage = DownloadTorrentStage.Imported;
                else if (it.TorrentId == importingId) stage = DownloadTorrentStage.Importing;
                else if (st is not null && (st.IsFinished || st.Progress >= 100)) stage = DownloadTorrentStage.Finishing;
                else stage = DownloadTorrentStage.Downloading;
                list.Add(new DownloadTorrentTelemetry
                {
                    Name = string.IsNullOrWhiteSpace(st?.Name) ? job.Title : st!.Name,
                    Stage = stage,
                    ProgressPercent = it.Imported ? 100 : (st?.Progress ?? 0),
                    DownloadRateBytesPerSec = stage == DownloadTorrentStage.Downloading ? (st?.DownloadRate ?? 0) : 0,
                    Seeds = st?.Seeds ?? 0,
                    Peers = st?.Peers ?? 0,
                    EtaSeconds = st is { Eta: > 0 } ? st.Eta : null,
                    TotalSizeBytes = st?.TotalSizeBytes ?? 0,
                    Season = it.Season,
                    Episode = it.Episode
                });
            }
            return list;
        }

        // Mark a single torrent as failed: record the reason, wipe its partial data (healthy siblings keep
        // going), and remember it so it's excluded from further polling and from the final "all imported" check.
        async Task FailTorrentAsync(TorrentItem it, string reason)
        {
            if (!failed.Add(it.TorrentId)) return;
            failReasons.Add(reason);
            logger.LogWarning("Job {JobId} torrent {TorrentId} (S{Season}E{Episode}) failed: {Reason}",
                job.Id, it.TorrentId, it.Season, it.Episode, reason);
            try { await downloadClient.RemoveAsync(it.TorrentId, removeData: true, ct); }
            catch (Exception ex) { logger.LogDebug(ex, "Cleanup of failed torrent {TorrentId} skipped", it.TorrentId); }
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Re-check VPN health during the in-flight job too, not just at claim time — if it drops
            // mid-download, hold off driving more indexer/Deluge/API traffic through it this tick rather
            // than plowing ahead as if nothing happened.
            if (!await vpn.IsHealthyAsync(ct))
            {
                logger.LogWarning("VPN unhealthy mid-job {JobId}; pausing this tick", job.Id);
                await Task.Delay(interval, ct);
                continue;
            }

            double progressSum = 0;

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.Imported) { progressSum += 100; continue; }
                if (failed.Contains(it.TorrentId)) continue; // dropped; excluded from the average

                var status = await downloadClient.GetStatusAsync(it.TorrentId, ct);
                if (status is null) { await FailTorrentAsync(it, "A torrent disappeared from the download client"); continue; }
                latest[it.TorrentId] = status;
                if (string.Equals(status.State, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    await FailTorrentAsync(it, "A torrent entered an error state"); continue;
                }

                var nowTick = DateTime.UtcNow;
                if (lastProgress.TryGetValue(it.TorrentId, out var last) && status.Progress > last.Progress)
                    lastProgress[it.TorrentId] = (status.Progress, nowTick);
                else if (lastProgress.TryGetValue(it.TorrentId, out var stuck) && nowTick - stuck.ChangedAt > stallTimeout)
                {
                    await FailTorrentAsync(it, $"Torrent stalled at {status.Progress:F0}% for over {stallTimeout.TotalMinutes:F0}m (no seeders?)");
                    continue;
                }

                // Pack trimmed to specific episodes: once Deluge has resolved the file list, deselect the
                // files we don't need so only the wanted episodes download. Best-effort and done once; the
                // importer also filters to NeededEpisodes, so this is purely a bandwidth/disk optimization.
                if (it is { IsPack: true, NeededEpisodes: { Count: > 0 } needed } && !trimmed.Contains(it.TorrentId) && status.Files.Count > 0)
                {
                    trimmed.Add(it.TorrentId);
                    var keepSet = needed.ToHashSet();
                    var keep = status.Files.Select(f =>
                    {
                        var ep = parser.Parse(Path.GetFileName(f)).Episode;
                        return ep is null || keepSet.Contains(ep.Value); // keep unmappable files (subs/extras) to be safe
                    }).ToList();
                    // Safety valve for a numbering mismatch (TMDB vs the pack's internal episode numbers,
                    // classic for kids'/preschool shows): if trimming would leave NOTHING to download, don't
                    // trim — take the whole pack rather than end up with an empty result.
                    if (!keep.Any(k => k))
                        logger.LogWarning("Job {JobId} torrent {TorrentId}: trimming to episode(s) {Needed} would skip every file (numbering mismatch?) — keeping the whole pack",
                            job.Id, it.TorrentId, string.Join(",", needed));
                    else if (keep.Any(k => !k) && await downloadClient.SetWantedFilesAsync(it.TorrentId, keep, ct))
                        logger.LogInformation("Job {JobId} torrent {TorrentId}: season pack trimmed to episode(s) {Needed} — downloading {Kept}/{Total} file(s)",
                            job.Id, it.TorrentId, string.Join(",", needed), keep.Count(k => k), keep.Count);
                }

                progressSum += status.Progress;

                if (status.IsFinished || status.Progress >= 100)
                {
                    var sourcePath = ResolveSourcePath(status, job.Id, it.TorrentId);
                    if (sourcePath is null)
                    {
                        // is_finished can lead the actual flush-to-disk; give the files a grace window to
                        // appear before treating an unresolvable path as a real failure.
                        var firstFinished = finishedSince.TryGetValue(it.TorrentId, out var t) ? t : (finishedSince[it.TorrentId] = nowTick);
                        if (nowTick - firstFinished < finishSettle)
                        {
                            logger.LogDebug("Job {JobId} torrent {TorrentId}: finished but on-disk path not resolvable yet; re-checking (waited {Elapsed:F0}s of {Grace:F0}s grace)",
                                job.Id, it.TorrentId, (nowTick - firstFinished).TotalSeconds, finishSettle.TotalSeconds);
                            continue;
                        }
                        await FailTorrentAsync(it, $"Could not resolve an on-disk path for the finished torrent after {finishSettle.TotalSeconds:F0}s (save_path={status.SavePath}, reported name=\"{status.Name}\")");
                        continue;
                    }
                    // Surface the "renaming & moving" phase in the admin panel before the (potentially slow,
                    // blocking) import so it doesn't look stuck at 100% while files are being transferred.
                    await SafeReportProgress(job.Id, (int)Math.Round(progressSum / Math.Max(1, items.Count)), BuildTelemetry(importingId: it.TorrentId));
                    var result = await importer.ImportAsync(job, it, sourcePath, ct);
                    if (!result.Success) { await FailTorrentAsync(it, result.FailReason ?? "A download completed but import failed"); continue; }

                    try
                    {
                        var files = result.Files.Select(f => new ImportedFileDto
                        {
                            TorrentId = it.TorrentId,
                            SourcePath = f.SourcePath,
                            DestinationPath = f.DestinationPath,
                            FileType = f.FileType,
                            SeasonNumber = f.Season,
                            EpisodeNumber = f.Episode,
                            SizeBytes = f.SizeBytes
                        }).ToList();
                        await api.ReportImportedFilesAsync(job.Id, files, ct);
                    }
                    catch (Exception ex) { logger.LogWarning(ex, "Could not persist import audit rows for job {JobId}", job.Id); }

                    try { await api.RefreshLibraryAsync(job.MediaType, ct); }
                    catch (Exception ex) { logger.LogDebug(ex, "Plex library refresh trigger skipped for job {JobId}", job.Id); }

                    progressSum += 100 - status.Progress; // count the just-imported torrent as fully done this tick
                    items[i] = it with { Imported = true };
                    await stateStore.SaveAsync(record with { Torrents = items.ToList() }, ct); // persist so a restart resumes
                    try { await downloadClient.RemoveAsync(it.TorrentId, removeData: false, ct); } // keep files for seeding
                    catch (Exception ex) { logger.LogDebug(ex, "Torrent removal after import skipped"); }
                }
            }

            await SafeReportProgress(job.Id, (int)Math.Round(progressSum / Math.Max(1, items.Count)), BuildTelemetry());

            // Terminal when every torrent has either imported or failed — no early abort on a single failure.
            if (items.All(x => x.Imported || failed.Contains(x.TorrentId))) break;

            await Task.Delay(interval, ct);
        }

        var importedCount = items.Count(x => x.Imported);
        if (importedCount == items.Count)
        {
            await SafeMarkFulfilled(job.MediaRequestId);
        }
        else if (importedCount > 0)
        {
            await SafePartiallyComplete(job.MediaRequestId,
                $"{importedCount}/{items.Count} downloads imported; the rest failed: {string.Join("; ", failReasons.Distinct())}");
        }
        else
        {
            await SafeFail(job.MediaRequestId, string.Join("; ", failReasons.Distinct()) is { Length: > 0 } r ? r : "All downloads failed");
        }
        await SafeRemoveState(job.Id);
    }

    /// <summary>
    /// Resolve the real on-disk source path for a finished torrent. A torrent's reported "name" is only
    /// a display hint (from the magnet's dn= or resolved metadata) and can legitimately differ from the
    /// actual file/folder Deluge wrote to disk — blindly trusting it (the old behavior) silently failed
    /// imports whenever they diverged, even though the download itself succeeded. Tries, in order:
    /// (1) Deluge's own reported file list — authoritative when available; (2) the old name-based guess;
    /// (3) a last-resort scan of the save directory for its most-recently-modified entry. Every fallback
    /// tier is logged so a mismatch is visible immediately instead of requiring DB archaeology.
    /// </summary>
    private string? ResolveSourcePath(Download.DownloadStatus status, int jobId, string torrentId)
    {
        var saveDir = status.SavePath ?? string.Empty;

        if (status.Files.Count > 0)
        {
            string? viaFiles = status.Files.Count == 1
                ? Path.Combine(saveDir, NormalizeRelative(status.Files[0]))
                : CommonFolder(status.Files) is { } folder ? Path.Combine(saveDir, folder) : saveDir;

            if (Directory.Exists(viaFiles) || File.Exists(viaFiles))
            {
                logger.LogDebug("Job {JobId} torrent {TorrentId}: resolved import source via Deluge's file list -> {Path}", jobId, torrentId, viaFiles);
                return viaFiles;
            }
            logger.LogWarning("Job {JobId} torrent {TorrentId}: Deluge reported {Count} file(s) but the derived path doesn't exist ({Path}); falling back to name-based resolution",
                jobId, torrentId, status.Files.Count, viaFiles);
        }

        var viaName = Path.Combine(saveDir, status.Name);
        if (Directory.Exists(viaName) || File.Exists(viaName))
        {
            if (status.Files.Count == 0)
                logger.LogDebug("Job {JobId} torrent {TorrentId}: resolved import source via reported name (no file list available) -> {Path}", jobId, torrentId, viaName);
            return viaName;
        }

        logger.LogWarning("Job {JobId} torrent {TorrentId}: reported name \"{Name}\" doesn't exist under {SaveDir} either; falling back to picking the most-recently-modified entry in the save directory",
            jobId, torrentId, status.Name, saveDir);

        if (Directory.Exists(saveDir))
        {
            var newest = Directory.EnumerateFileSystemEntries(saveDir)
                .Select(p => new { Path = p, Modified = SafeLastWrite(p) })
                .OrderByDescending(x => x.Modified)
                .FirstOrDefault();
            if (newest is not null)
            {
                logger.LogWarning("Job {JobId} torrent {TorrentId}: heuristic fallback picked \"{Path}\" (most recently modified in {SaveDir}) — verify this is correct",
                    jobId, torrentId, newest.Path, saveDir);
                return newest.Path;
            }
        }

        return null;
    }

    private static string NormalizeRelative(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    // The shared top-level folder across a multi-file torrent's relative paths, if there is exactly one
    // (the normal case — a season pack's files all live under "Show.Name.S01/..."). Null when files sit
    // flat at the torrent root with no common containing folder.
    private static string? CommonFolder(IReadOnlyList<string> relativePaths)
    {
        string? common = null;
        foreach (var rel in relativePaths)
        {
            var normalized = NormalizeRelative(rel);
            var firstSegment = normalized.Split(Path.DirectorySeparatorChar, 2)[0];
            if (string.IsNullOrEmpty(firstSegment)) return null;
            if (common is null) common = firstSegment;
            else if (!string.Equals(common, firstSegment, StringComparison.Ordinal)) return null;
        }
        return common;
    }

    private static DateTime SafeLastWrite(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private async Task SafeFail(int requestId, string reason)
    {
        try { await api.MarkFailedAsync(requestId, reason, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not report failure for request {RequestId}", requestId); }
    }

    private async Task SafePartiallyComplete(int requestId, string reason)
    {
        try { await api.MarkPartiallyCompletedAsync(requestId, reason, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not report partial completion for request {RequestId}", requestId); }
    }

    // A transient network blip on the SUCCESS path (progress report / fulfilled callback) shouldn't
    // read as a job failure — these are best-effort status pushes, not the thing that determines whether
    // the download actually succeeded, so failures here are logged and swallowed rather than propagated
    // up to the outer catch-all (which would otherwise mark an actually-successful job Failed and delete
    // its resumable state).
    private async Task SafeReportProgress(int jobId, int progress, IReadOnlyList<DownloadTorrentTelemetry>? torrents = null)
    {
        try { await api.ReportProgressAsync(jobId, progress, torrents, CancellationToken.None); }
        catch (Exception ex) { logger.LogDebug(ex, "Progress report skipped for job {JobId}", jobId); }
    }

    private async Task SafeMarkFulfilled(int requestId)
    {
        try { await api.MarkFulfilledAsync(requestId, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not report fulfillment for request {RequestId}; will retry next reconciliation pass", requestId); }
    }

    private async Task SafeRemoveState(int jobId)
    {
        try { await stateStore.RemoveAsync(jobId, CancellationToken.None); }
        catch (Exception ex) { logger.LogDebug(ex, "State cleanup skipped for job {JobId}", jobId); }
    }
}
