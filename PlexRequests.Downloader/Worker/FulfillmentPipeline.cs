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
using PlexRequestsHosted.Shared.Releases;

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
    ITorrentImportCoordinator importCoordinator,
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

            // A manually grabbed release skips searching and ranking outright. Forcing a grab is precisely
            // an override of that judgement, so re-deriving it would either waste a search or quietly
            // substitute a different release than the admin picked.
            DownloadPlan plan;
            IndexerSearchResult search = IndexerSearchResult.Empty;
            IReadOnlyList<ReleaseCandidate> candidates = Array.Empty<ReleaseCandidate>();
            if (job.IsManualGrab && !string.IsNullOrWhiteSpace(job.ForcedMagnet))
            {
                logger.LogInformation("Job {JobId} \"{Title}\": manual grab of \"{Release}\" — skipping search",
                    job.Id, job.Title, job.ForcedReleaseName);
                plan = BuildForcedPlan(job);
            }
            else
            {
                search = await indexer.SearchAsync(job, ct);
                candidates = search.Candidates;
                plan = ranker.PlanDownload(candidates, job);
            }
            if (plan.IsEmpty)
            {
                // Never dead-end: a normal job is parked and re-searched on a backoff (request shows
                // "Searching", not "Failed"); an upgrade job simply found nothing better and stops quietly.
                // The deferral reason carries the per-indexer breakdown so the admin Missing panel answers
                // "which indexers returned nothing?" without log archaeology.
                var detail = candidates.Count == 0
                    ? $"No indexer returned a release ({search.Summary})"
                    : $"{candidates.Count} candidate(s) all rejected by quality/seeder/title filters ({search.Summary})";
                if (job.IsUpgrade) await api.MarkUpgradeExhaustedAsync(job.Id, ct);
                // Flag whether the search had anything to reject. Only that case counts toward relaxing the
                // quality target — a title that simply isn't out yet gains nothing from lowering the bar.
                else await api.MarkDeferredAsync(job.Id, detail, candidates.Count > 0, ct);
                return;
            }

            var label = job.MediaType switch
            {
                MediaType.Movie => deluge.Value.MovieLabel,
                MediaType.Music => deluge.Value.MusicLabel,
                _ => deluge.Value.TvLabel
            };
            var torrents = new List<TorrentItem>();
            foreach (var item in plan.Items)
            {
                var torrentId = await downloadClient.AddMagnetAsync(item.Candidate.Magnet, label, ct);
                if (string.IsNullOrWhiteSpace(torrentId))
                {
                    logger.LogWarning("Failed to add magnet for job {JobId} (S{Season}E{Episode})", job.Id, item.Season, item.Episode);
                    continue;
                }
                torrents.Add(new TorrentItem(
                    torrentId,
                    item.Season,
                    item.Episode,
                    item.IsPack,
                    NeededEpisodes: item.NeededEpisodes,
                    Resolution: item.Resolution,
                    Source: item.Candidate.Source,
                    IndexerId: item.Candidate.IndexerId > 0 ? item.Candidate.IndexerId : null));
            }

            if (torrents.Count == 0)
            {
                // Adding to the download client failed for everything (usually a transient Deluge/VPN blip).
                // Defer with a backoff rather than failing so it's retried automatically.
                if (job.IsUpgrade) await api.MarkUpgradeExhaustedAsync(job.Id, ct);
                else await api.MarkDeferredAsync(job.Id, "Could not add torrent(s) to the download client", false, ct);
                return;
            }

            logger.LogInformation("Job {JobId} \"{Title}\": added {Count} torrent(s) [{Kind}]", job.Id, job.Title, torrents.Count, plan.Kind);

            // Persist the job<->torrent link BEFORE monitoring starts. The local state file written below
            // only helps THIS process; this is what lets any process — the reconciler after a deploy, or a
            // replacement worker — pick these up. Without it, torrents added here became invisible the
            // moment this worker was replaced, which is how sixty-seven of them ended up downloading with
            // their job marked "no acceptable release found".
            await api.RegisterTorrentsAsync(job.Id, torrents.Select((t, i) => new TrackedTorrentDto
            {
                FulfillmentJobId = job.Id,
                TorrentId = t.TorrentId,
                InfoHash = MagnetUtil.InfoHashFromMagnet(plan.Items.ElementAtOrDefault(i)?.Candidate.Magnet ?? string.Empty)
                           ?? MagnetUtil.Normalize(t.TorrentId),
                ReleaseName = plan.Items.ElementAtOrDefault(i)?.Candidate.ReleaseName,
                Source = plan.Items.ElementAtOrDefault(i)?.Candidate.Source,
                IndexerId = plan.Items.ElementAtOrDefault(i)?.Candidate.IndexerId is > 0
                    ? plan.Items[i].Candidate.IndexerId
                    : null,
                Season = t.Season,
                Episode = t.Episode,
                IsPack = t.IsPack,
                NeededEpisodes = t.NeededEpisodes?.ToList() ?? new(),
                Resolution = t.Resolution
            }).ToList(), ct);

            var record = new ActiveJobRecord(job, torrents);
            await stateStore.SaveAsync(record, ct);
            await SafeReportProgress(job.Id, 0);
            await MonitorAndImportAllAsync(record, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pipeline error for job {JobId}", job.Id);
            // Never dead-end on an exception either: errors that reach here are overwhelmingly transient
            // (Deluge/VPN/indexer/web-API hiccups), so park the job for re-search on the same backoff as
            // an empty search — permanently failing the request meant a single blip ended its retries.
            // An upgrade job's request is already Available; just stop this attempt.
            if (job.IsUpgrade) await SafeMarkUpgradeExhausted(job.Id);
            else await SafeDefer(job.Id, $"Downloader error: {ex.Message}");
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
            if (record.Job.IsUpgrade) await SafeMarkUpgradeExhausted(record.Job.Id);
            else await SafeDefer(record.Job.Id, $"Downloader error on resume: {ex.Message}");
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
        // Every library destination path written this run — used (upgrade jobs only) to avoid deleting an old
        // file that the new import just overwrote in place.
        var importedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    Source = it.Source,
                    IndexerId = it.IndexerId,
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
        async Task FailTorrentAsync(TorrentItem it, string reason, BlocklistReason blocklistReason = BlocklistReason.DownloadFailed)
        {
            if (!failed.Add(it.TorrentId)) return;
            failReasons.Add(reason);
            logger.LogWarning("Job {JobId} torrent {TorrentId} (S{Season}E{Episode}) failed: {Reason}",
                job.Id, it.TorrentId, it.Season, it.Episode, reason);

            // Remember the release so a re-search can't rank the same broken torrent top again and fail
            // identically on every backoff tick. Best-effort — this must never change the job's outcome.
            latest.TryGetValue(it.TorrentId, out var status);
            await SafeBlocklist(job.Id, new BlocklistRequestDto
            {
                InfoHash = it.TorrentId,
                ReleaseName = status?.Name ?? job.ForcedReleaseName ?? job.Title,
                Reason = blocklistReason,
                Detail = reason,
                Season = it.Season,
                Episode = it.Episode
            });
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
                if (status is null) { await FailTorrentAsync(it, "A torrent disappeared from the download client", BlocklistReason.TorrentError); continue; }
                latest[it.TorrentId] = status;
                if (string.Equals(status.State, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    await FailTorrentAsync(it, "A torrent entered an error state", BlocklistReason.TorrentError); continue;
                }

                var nowTick = DateTime.UtcNow;
                if (lastProgress.TryGetValue(it.TorrentId, out var last) && status.Progress > last.Progress)
                    lastProgress[it.TorrentId] = (status.Progress, nowTick);
                else if (lastProgress.TryGetValue(it.TorrentId, out var stuck) && nowTick - stuck.ChangedAt > stallTimeout)
                {
                    await FailTorrentAsync(it, $"Torrent stalled at {status.Progress:F0}% for over {stallTimeout.TotalMinutes:F0}m (no seeders?)", BlocklistReason.Stalled);
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
                    // classic for kids'/preschool shows). This used to fire only when trimming would keep
                    // NOTHING, which missed the far more common partial mismatch: needing 13 episodes but
                    // matching only 1 left the other 12 deselected and the request silently short. Bail out
                    // whenever we'd keep fewer files than episodes we're supposed to be fetching.
                    int wouldKeep = keep.Count(k => k);
                    if (wouldKeep < needed.Count)
                    {
                        logger.LogWarning("Job {JobId} torrent {TorrentId}: trimming to episode(s) {Needed} would keep only {Kept} file(s) (numbering mismatch?) — keeping the whole pack",
                            job.Id, it.TorrentId, string.Join(",", needed), wouldKeep);
                        // Clear the restriction so the importer's own NeededEpisodes filter also stands down;
                        // otherwise it would re-apply the same partial match at import time.
                        items[i] = it with { NeededEpisodes = null };
                    }
                    else if (keep.Any(k => !k) && await downloadClient.SetWantedFilesAsync(it.TorrentId, keep, ct))
                        logger.LogInformation("Job {JobId} torrent {TorrentId}: season pack trimmed to episode(s) {Needed} — downloading {Kept}/{Total} file(s)",
                            job.Id, it.TorrentId, string.Join(",", needed), keep.Count(k => k), keep.Count);
                }

                progressSum += status.Progress;

                if (status.IsFinished || status.Progress >= 100)
                {
                    var sourcePath = ImportSourceResolver.Resolve(status, job.Id, it.TorrentId, logger);
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
                        await FailTorrentAsync(it, $"Could not resolve an on-disk path for the finished torrent after {finishSettle.TotalSeconds:F0}s (save_path={status.SavePath}, reported name=\"{status.Name}\")", BlocklistReason.PathUnresolvable);
                        continue;
                    }
                    // Surface the "renaming & moving" phase in the admin panel before the (potentially slow,
                    // blocking) import so it doesn't look stuck at 100% while files are being transferred.
                    await SafeReportProgress(job.Id, (int)Math.Round(progressSum / Math.Max(1, items.Count)), BuildTelemetry(importingId: it.TorrentId));
                    var result = await importCoordinator.RunOnceAsync(it.TorrentId,
                        token => importer.ImportAsync(job, it, sourcePath, token), ct);
                    if (!result.Success) { await FailTorrentAsync(it, result.FailReason ?? "A download completed but import failed", BlocklistReason.ImportFailed); continue; }

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
                            SizeBytes = f.SizeBytes,
                            ResolutionHeight = it.Resolution // resolution the ranker chose for this torrent
                        }).ToList();
                        await api.ReportImportedFilesAsync(job.Id, files, ct);
                        foreach (var f in files) importedDestinations.Add(f.DestinationPath);
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

        if (job.IsUpgrade)
        {
            // Upgrade job: the request is already Available, so there's no "fail" outcome — either we imported
            // a better release (replace the old files + report the upgrade) or we got nothing (exhausted).
            if (importedCount > 0)
            {
                DeleteReplacedFiles(job, importedDestinations);
                await SafeMarkUpgraded(job.Id);
            }
            else
            {
                await SafeMarkUpgradeExhausted(job.Id);
            }
        }
        else if (importedCount == items.Count)
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
            // No hard "Failed" for a first-time grab either: park it and keep searching on a backoff.
            var reason = string.Join("; ", failReasons.Distinct()) is { Length: > 0 } r ? r : "All downloads failed";
            await SafeDefer(job.Id, reason);
        }
        await SafeRemoveState(job.Id);
    }

    // Physically remove the old files an upgrade superseded, EXCEPT any the new import overwrote in place
    // (same destination path). Best-effort and confined to the recorded ReplacePaths — never deletes anything
    // the new import didn't produce. Only called on an upgrade that imported at least one file.
    private void DeleteReplacedFiles(FulfillmentJobDto job, HashSet<string> importedDestinations)
    {
        foreach (var path in job.ReplacePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || importedDestinations.Contains(path)) continue;
            try
            {
                if (File.Exists(path)) { File.Delete(path); logger.LogInformation("Upgrade {JobId}: removed superseded file {Path}", job.Id, path); }
            }
            catch (Exception ex) { logger.LogWarning(ex, "Upgrade {JobId}: could not delete superseded file {Path}", job.Id, path); }
        }
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

    /// <summary>
    /// A one-item plan from the release an admin explicitly chose. Season/episode come from the job's own
    /// targets rather than being re-parsed from the name — the admin already told us what this is for.
    /// </summary>
    private static DownloadPlan BuildForcedPlan(FulfillmentJobDto job)
    {
        var episode = job.RequestedEpisodes.FirstOrDefault();
        var season = episode?.Season ?? job.RequestedSeasons.FirstOrDefault();
        var target = job.SeasonTargets.FirstOrDefault();
        bool isPack = episode is null;

        var candidate = new ReleaseCandidate
        {
            ReleaseName = job.ForcedReleaseName ?? job.Title,
            Magnet = job.ForcedMagnet!,
            InfoHash = MagnetUtil.InfoHashFromMagnet(job.ForcedMagnet),
            IndexerId = job.ForcedIndexerId ?? 0,
            Source = "manual"
        };

        var item = new DownloadPlanItem(candidate, season == 0 ? null : season, episode?.Episode, isPack)
        {
            NeededEpisodes = isPack && target is { MissingEpisodes.Count: > 0 } ? target.MissingEpisodes : null
        };
        return new DownloadPlan(isPack ? DownloadPlanKind.SeasonPack : DownloadPlanKind.Episodes, new[] { item });
    }

    private async Task SafeBlocklist(int jobId, BlocklistRequestDto request)
    {
        try { await api.BlocklistAsync(jobId, request, CancellationToken.None); }
        catch (Exception ex) { logger.LogDebug(ex, "Blocklist entry skipped for job {JobId}", jobId); }
    }

    // Park a normal job for re-search instead of failing it (the never-dead-end path).
    private async Task SafeDefer(int jobId, string reason, bool candidatesRejected = false)
    {
        try { await api.MarkDeferredAsync(jobId, reason, candidatesRejected, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not report deferral for job {JobId}", jobId); }
    }

    private async Task SafeMarkUpgraded(int jobId)
    {
        try { await api.MarkUpgradedAsync(jobId, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not report upgrade success for job {JobId}", jobId); }
    }

    private async Task SafeMarkUpgradeExhausted(int jobId)
    {
        try { await api.MarkUpgradeExhaustedAsync(jobId, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not report upgrade-exhausted for job {JobId}", jobId); }
    }

    private async Task SafeRemoveState(int jobId)
    {
        try { await stateStore.RemoveAsync(jobId, CancellationToken.None); }
        catch (Exception ex) { logger.LogDebug(ex, "State cleanup skipped for job {JobId}", jobId); }
    }
}
