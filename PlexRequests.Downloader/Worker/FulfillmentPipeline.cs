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
using PlexRequestsHosted.Shared;

namespace PlexRequests.Downloader.Worker;

public interface IFulfillmentPipeline
{
    Task ProcessAsync(FulfillmentJobDto job, CancellationToken ct);
    Task ResumeAsync(ActiveJobRecord record, CancellationToken ct);
}

internal sealed record CanonicalPackFileSelection(
    IReadOnlyList<bool> Keep,
    IReadOnlyList<(int Season, int Episode)> MissingCoverage);

internal sealed record PreparedDownloadItem(
    DownloadPlanItem Item,
    IAcquisitionBackend Backend,
    AcquisitionManifest? Manifest,
    IReadOnlyList<bool>? WantedFiles,
    int? FractionalEpisodeInsertionAfter);

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
    IAcquisitionBackendRegistry acquisitionBackends,
    ILibraryImporter importer,
    ITransferImportCoordinator importCoordinator,
    IPostImportCleanup postImportCleanup,
    IStorageSafetyService storageSafety,
    IPlexRequestsApiClient api,
    IJobStateStore stateStore,
    IVpnGuard vpn,
    IOptions<DelugeOptions> deluge,
    IOptions<WorkerOptions> worker,
    IOptions<StorageOptions> storageOptions,
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
            string? episodeOrderDiscoveryDetail = null;
            if (plan.IsEmpty && !job.IsManualGrab && job.IsAnime
                && job.EpisodeOrderProfile is null
                && (job.EpisodeOrderCandidates.Count > 0 || job.CanonicalSeasons.Count > 0)
                && ranker.ManifestFallbackCandidates.Count > 0)
            {
                (plan, episodeOrderDiscoveryDetail) = await TryResolveAnimeEpisodeOrderAsync(job, ct);
            }
            if (plan.IsEmpty)
            {
                // Never dead-end: a normal job is parked and re-searched on a backoff (request shows
                // "Searching", not "Failed"); an upgrade job simply found nothing better and stops quietly.
                // The deferral reason carries the per-indexer breakdown so the admin Missing panel answers
                // "which indexers returned nothing?" without log archaeology.
                var detail = candidates.Count == 0
                    ? $"No indexer returned a release ({search.Summary})"
                    : !string.IsNullOrWhiteSpace(episodeOrderDiscoveryDetail)
                        ? $"{episodeOrderDiscoveryDetail} ({search.Summary})"
                    : !string.IsNullOrWhiteSpace(ranker.LastFailureSummary)
                        ? $"{ranker.LastFailureSummary} ({search.Summary})"
                        : $"{candidates.Count} candidate(s) could not form a safe download plan ({search.Summary})";
                if (job.IsUpgrade && !job.IsReplacement) await api.MarkUpgradeExhaustedAsync(job.Id, ct);
                // Flag whether the search had anything to reject. Only that case counts toward relaxing the
                // quality target — a title that simply isn't out yet gains nothing from lowering the bar.
                else await api.MarkDeferredAsync(job.Id, detail, ranker.LastSearchRejectedCandidates, ct);
                return;
            }

            var label = job.MediaType switch
            {
                MediaType.Movie => deluge.Value.MovieLabel,
                MediaType.Music => deluge.Value.MusicLabel,
                _ => deluge.Value.TvLabel
            };
            var prepared = new List<PreparedDownloadItem>();
            var transfers = new List<TransferItem>();
            var preflightFailures = new List<string>();
            foreach (var plannedItem in plan.Items)
            {
                var item = plannedItem;
                var resource = item.Candidate.Acquisition;
                if (!acquisitionBackends.TryGet(resource.Protocol, out var backend))
                {
                    logger.LogWarning("Job {JobId}: no acquisition backend is configured for {Protocol}", job.Id, resource.Protocol);
                    continue;
                }
                AcquisitionManifest? manifest = null;
                IReadOnlyList<bool>? wantedFiles = null;
                int? fractionalEpisodeInsertionAfter = null;
                if (item.RequiresManifestPreflight)
                {
                    if (!backend.Capabilities.SupportsManifestPreflight)
                    {
                        var detail = $"{item.Candidate.ReleaseName}: {resource.Protocol} cannot preflight collection manifests";
                        preflightFailures.Add(detail);
                        logger.LogWarning("Job {JobId}: {Detail}", job.Id, detail);
                        continue;
                    }

                    try
                    {
                        manifest = await backend.GetManifestAsync(resource, ct);
                        if (manifest is null)
                        {
                            var detail = $"{item.Candidate.ReleaseName}: torrent metadata was not available within the preflight timeout";
                            preflightFailures.Add(detail);
                            logger.LogWarning("Job {JobId}: {Detail}", job.Id, detail);
                            continue;
                        }

                        var maxPackGb = job.QualityProfile?.MaxSeasonPackSizeGb
                            ?? prefs.Current.MaxSeasonPackSizeGb;
                        var decision = AnimeManifestPreflight.Evaluate(manifest, job, item, parser,
                            libraryPrefs.Current.VideoExtensions, maxPackGb,
                            libraryPrefs.Current.SubtitleExtensions);
                        if (!decision.Accepted && item.Season is null)
                        {
                            // A manually chosen or already-planned collection may prove only some named
                            // seasons. Keep that safe progress and let the durable partial continuation find
                            // the remaining arcs; never widen this to individual files from an ambiguous season.
                            var namedPlan = TryPlanNamedAnimeCollection(manifest, job, item, parser,
                                libraryPrefs.Current.VideoExtensions, maxPackGb,
                                libraryPrefs.Current.SubtitleExtensions);
                            if (namedPlan is not null)
                            {
                                item = namedPlan.Value.Plan.Items[0];
                                decision = namedPlan.Value.Decision;
                                plan = plan with
                                {
                                    CoversAllTargets = plan.CoversAllTargets
                                                       && namedPlan.Value.Plan.CoversAllTargets
                                };
                            }
                        }
                        if (!decision.Accepted)
                        {
                            var detail = $"{item.Candidate.ReleaseName}: {decision.Detail}";
                            preflightFailures.Add(detail);
                            logger.LogWarning("Job {JobId}: anime manifest rejected — {Detail}", job.Id, detail);
                            await SafeBlocklist(job.Id, new BlocklistRequestDto
                            {
                                InfoHash = resource.SourceId ?? MagnetUtil.InfoHashFromMagnet(resource.Locator),
                                Protocol = resource.Protocol,
                                SourceId = resource.SourceId,
                                ReleaseName = item.Candidate.ReleaseName,
                                Reason = BlocklistReason.EpisodeMappingAmbiguous,
                                Detail = decision.Detail,
                                Season = item.Season,
                                Episode = item.Episode,
                                IndexerId = item.Candidate.IndexerId > 0 ? item.Candidate.IndexerId : null
                            });
                            continue;
                        }

                        wantedFiles = decision.WantedFiles;
                        fractionalEpisodeInsertionAfter = decision.FractionalEpisodeInsertionAfter;
                        logger.LogInformation("Job {JobId}: anime collection preflight accepted \"{Release}\" — {Detail}",
                            job.Id, item.Candidate.ReleaseName, decision.Detail);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        var detail = $"{item.Candidate.ReleaseName}: manifest preflight failed ({ex.Message})";
                        preflightFailures.Add(detail);
                        logger.LogWarning(ex, "Job {JobId}: {Detail}", job.Id, detail);
                        continue;
                    }
                }

                prepared.Add(new PreparedDownloadItem(item, backend, manifest, wantedFiles,
                    fractionalEpisodeInsertionAfter));
            }

            if (prepared.Count == 0)
            {
                var detail = preflightFailures.Count > 0
                    ? $"No anime collection passed manifest preflight: {string.Join("; ", preflightFailures.Take(3))}"
                    : "No release has an available acquisition backend";
                if (job.IsUpgrade && !job.IsReplacement) await api.MarkUpgradeExhaustedAsync(job.Id, ct);
                else await api.MarkDeferredAsync(job.Id, detail, false, ct);
                return;
            }

            var payloadBytes = prepared.Sum(x => EstimatedPayloadBytes(x, job.MediaType, storageOptions.Value));
            var destinationRoot = libraryPrefs.Current.Resolve(job, job.MediaType, isEpisode: false).Root;
            await using var storageReservation = await storageSafety.TryReserveAsync(
                job, payloadBytes, destinationRoot, libraryPrefs.Current, ct);
            if (!storageReservation.Admission.Allowed)
            {
                await api.MarkDeferredAsync(job.Id, storageReservation.Admission.Detail, false, ct);
                return;
            }

            foreach (var preparedItem in prepared)
            {
                var item = preparedItem.Item;
                var resource = item.Candidate.Acquisition;
                var transferId = await preparedItem.Backend.EnqueueAsync(new AcquisitionRequest(resource, label,
                    item.Candidate.ReleaseName,
                    job.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    preparedItem.Manifest, preparedItem.WantedFiles), ct);
                if (string.IsNullOrWhiteSpace(transferId))
                {
                    logger.LogWarning("Failed to enqueue {Protocol} transfer for job {JobId} (S{Season}E{Episode})",
                        resource.Protocol, job.Id, item.Season, item.Episode);
                    if (item.RequiresManifestPreflight)
                        preflightFailures.Add(
                            $"{item.Candidate.ReleaseName}: verified selection could not be added (the torrent may already be owned by another job)");
                    continue;
                }
                transfers.Add(new TransferItem(
                    transferId,
                    item.Season,
                    item.Episode,
                    item.IsPack,
                    NeededEpisodes: item.NeededEpisodes,
                    Resolution: item.Resolution,
                    Source: item.Candidate.Source,
                    IndexerId: item.Candidate.IndexerId > 0 ? item.Candidate.IndexerId : null,
                    ReleaseName: item.Candidate.ReleaseName,
                    Protocol: resource.Protocol,
                    SourceId: resource.SourceId,
                    NeededEpisodeRefs: item.NeededEpisodeRefs,
                    SourceSeason: item.SourceSeason,
                    FractionalEpisodeInsertionAfter: preparedItem.FractionalEpisodeInsertionAfter));
            }

            if (transfers.Count == 0)
            {
                // Adding to the download client failed for everything. Preserve manifest diagnostics when
                // present so the admin sees a mapping/coverage problem rather than a generic Deluge error.
                var detail = preflightFailures.Count > 0
                    ? $"No anime collection passed manifest preflight: {string.Join("; ", preflightFailures.Take(3))}"
                    : "Could not enqueue release(s) with an available acquisition backend";
                if (job.IsUpgrade && !job.IsReplacement) await api.MarkUpgradeExhaustedAsync(job.Id, ct);
                // DeferCount still drives the one-time admin escalation. Do not call a manifest/backend
                // failure a quality rejection: lowering the quality floor cannot repair it.
                else await api.MarkDeferredAsync(job.Id, detail, false, ct);
                return;
            }

            logger.LogInformation("Job {JobId} \"{Title}\": enqueued {Count} transfer(s) [{Kind}]", job.Id, job.Title, transfers.Count, plan.Kind);

            // Persist the job<->torrent link BEFORE monitoring starts. The local state file written below
            // only helps THIS process; this is what lets any process — the reconciler after a deploy, or a
            // replacement worker — pick these up. Without it, torrents added here became invisible the
            // moment this worker was replaced, which is how sixty-seven of them ended up downloading with
            // their job marked "no acceptable release found".
            await api.RegisterTransfersAsync(job.Id, transfers.Select(t => new TrackedTransferDto
            {
                FulfillmentJobId = job.Id,
                TransferId = t.TransferId,
                Protocol = t.Protocol,
                SourceId = t.SourceId,
                ReleaseName = t.ReleaseName,
                Source = t.Source,
                IndexerId = t.IndexerId,
                Season = t.Season,
                SourceSeason = t.SourceSeason,
                FractionalEpisodeInsertionAfter = t.FractionalEpisodeInsertionAfter,
                Episode = t.Episode,
                IsPack = t.IsPack,
                NeededEpisodes = t.NeededEpisodes?.ToList() ?? new(),
                NeededEpisodeRefs = t.NeededEpisodeRefs?.ToList() ?? new(),
                Resolution = t.Resolution
            }).ToList(), ct);

            // A mixed plan can lose one item to an unavailable backend or failed preflight. The remaining
            // transfers may still import, but they cannot truthfully complete the entire request.
            var record = new ActiveJobRecord(job, transfers,
                plan.CoversAllTargets && transfers.Count == plan.Items.Count,
                storageReservation.Admission.Reservations);
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
            if (job.IsUpgrade && !job.IsReplacement) await SafeMarkUpgradeExhausted(job.Id);
            else await SafeDefer(job.Id, $"Downloader error: {ex.Message}");
            await SafeRemoveState(job.Id);
        }
    }

    public async Task ResumeAsync(ActiveJobRecord record, CancellationToken ct)
    {
        try
        {
            logger.LogInformation("Resuming job {JobId} ({Count} transfer(s))", record.Job.Id, record.Transfers.Count);
            await MonitorAndImportAllAsync(record, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resume error for job {JobId}", record.Job.Id);
            if (record.Job.IsUpgrade && !record.Job.IsReplacement) await SafeMarkUpgradeExhausted(record.Job.Id);
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
        var items = record.Transfers.ToList(); // working copy; entries replaced as they import
        var interval = TimeSpan.FromSeconds(Math.Max(5, worker.Value.MonitorIntervalSeconds));
        var stallTimeout = TimeSpan.FromMinutes(Math.Max(5, worker.Value.StallTimeoutMinutes));
        var finishSettle = TimeSpan.FromSeconds(Math.Max(5, worker.Value.FinishSettleSeconds));
        var missingGrace = TimeSpan.FromSeconds(Math.Max(
            finishSettle.TotalSeconds, interval.TotalSeconds * 2));
        // Per-torrent stall tracking: last progress value seen + when it last changed.
        var lastProgress = new Dictionary<string, (double Progress, DateTime ChangedAt)>();
        // Per-torrent finish-settle tracking: when the torrent first reported finished (to grace the
        // is_finished-before-flush race before declaring a path-resolution failure).
        var finishedSince = new Dictionary<string, DateTime>();
        // Backend cleanup deliberately makes a successfully imported transfer disappear. Give the durable
        // import audit time to become visible before treating absence as a real failure.
        var missingSince = new Dictionary<string, DateTime>();
        // Torrents that hit a terminal failure — dropped, but do not abort the rest of the batch.
        var failed = new HashSet<string>();
        var failReasons = new List<string>();
        // Season packs restricted to specific episodes get their unwanted files deselected in Deluge once
        // metadata resolves — tracked here so we only apply it once per torrent.
        var trimmed = new HashSet<string>();
        // Latest raw client status per torrent, kept so we can assemble a live telemetry snapshot for the
        // admin downloads panel at any point in the tick (including right before a blocking import).
        var latest = new Dictionary<(AcquisitionProtocol, string), TransferStatus>();
        // Every library destination written across this job's retries — used by replacement cleanup to avoid
        // deleting a new file that overwrote the old path in an earlier partial attempt.
        var importedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now0 = DateTime.UtcNow;
        foreach (var it in items) lastProgress[TransferKey(it)] = (0, now0);

        async Task<bool> AdoptDurableImportsAsync()
        {
            var audit = await api.GetImportedFilesAsync(job.Id, ct);
            if (audit is null) return false;

            foreach (var file in audit)
                if (!string.IsNullOrWhiteSpace(file.DestinationPath))
                    importedDestinations.Add(file.DestinationPath);

            var changed = false;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Imported || !ImportAuditCoverage.Covers(items[i].Protocol,
                        items[i].TransferId, items[i].NeededEpisodeRefs, items[i].Season,
                        items[i].Episode, items[i].NeededEpisodes, audit)) continue;
                items[i] = items[i] with { Imported = true };
                missingSince.Remove(TransferKey(items[i]));
                changed = true;
                logger.LogInformation(
                    "Job {JobId} transfer {TransferId}: adopted durable import completed by another observer",
                    job.Id, items[i].TransferId);
            }

            if (changed)
                await stateStore.SaveAsync(record with { Transfers = items.ToList() }, ct);
            return true;
        }

        await AdoptDurableImportsAsync();

        // Assemble the per-torrent telemetry snapshot pushed up with each progress report. Failed torrents
        // are omitted; a torrent named in importingId is forced to the Importing stage (it's mid-move). This
        // is display-only — never drives control flow — so unknown/missing fields just read as 0.
        List<DownloadTransferTelemetry> BuildTelemetry(string? importingId = null)
        {
            var list = new List<DownloadTransferTelemetry>();
            foreach (var it in items)
            {
                var key = TransferKey(it);
                if (failed.Contains(key)) continue;
                latest.TryGetValue((it.Protocol, it.TransferId), out var st);
                DownloadTransferStage stage;
                if (it.Imported) stage = DownloadTransferStage.Imported;
                else if (key == importingId) stage = DownloadTransferStage.Importing;
                else if (st is not null && (st.IsFinished || st.Progress >= 100)) stage = DownloadTransferStage.Finishing;
                else stage = DownloadTransferStage.Downloading;
                list.Add(new DownloadTransferTelemetry
                {
                    Name = string.IsNullOrWhiteSpace(st?.Name) ? job.Title : st!.Name,
                    Protocol = it.Protocol,
                    Source = it.Source,
                    IndexerId = it.IndexerId,
                    Stage = stage,
                    ProgressPercent = it.Imported ? 100 : (st?.Progress ?? 0),
                    DownloadRateBytesPerSec = stage == DownloadTransferStage.Downloading ? (st?.DownloadRate ?? 0) : 0,
                    Seeds = st?.Seeds ?? 0,
                    Peers = st?.Peers ?? 0,
                    EtaSeconds = st?.Eta,
                    TotalSizeBytes = st?.TotalSizeBytes ?? 0,
                    Season = it.Season,
                    Episode = it.Episode
                });
            }
            return list;
        }

        // Mark a single torrent as failed: record the reason, wipe its partial data (healthy siblings keep
        // going), and remember it so it's excluded from further polling and from the final "all imported" check.
        async Task FailTransferAsync(TransferItem it, string reason, BlocklistReason blocklistReason = BlocklistReason.DownloadFailed)
        {
            var key = TransferKey(it);
            if (!failed.Add(key)) return;
            failReasons.Add(reason);
            logger.LogWarning("Job {JobId} transfer {TransferId} (S{Season}E{Episode}) failed: {Reason}",
                job.Id, it.TransferId, it.Season, it.Episode, reason);

            // Remember the release so a re-search can't rank the same broken torrent top again and fail
            // identically on every backoff tick. Best-effort — this must never change the job's outcome.
            latest.TryGetValue((it.Protocol, it.TransferId), out var status);
            await SafeBlocklist(job.Id, new BlocklistRequestDto
            {
                InfoHash = it.Protocol == AcquisitionProtocol.Torrent ? it.SourceId ?? it.TransferId : null,
                Protocol = it.Protocol,
                SourceId = it.SourceId,
                ReleaseName = status?.Name ?? job.ForcedReleaseName ?? job.Title,
                Reason = blocklistReason,
                Detail = reason,
                Season = it.Season,
                Episode = it.Episode,
                // A removed video should fall back to torrents, but a transient extractor/network failure
                // must heal without admin intervention. Retry the authoritative source after a cooldown.
                ExpiresAt = it.Protocol == AcquisitionProtocol.DirectAudio
                    ? DateTime.UtcNow.AddHours(6) : null
            });
            if (acquisitionBackends.TryGet(it.Protocol, out var backend))
            {
                try { await backend.RemoveAsync(it.TransferId, removeData: true, ct); }
                catch (Exception ex) { logger.LogDebug(ex, "Cleanup of failed transfer {TransferId} skipped", it.TransferId); }
            }
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // The reconciler may have completed an import between monitor ticks. Adopt it before asking the
            // backend, because successful cleanup means the backend is expected to return "not found".
            var auditAvailable = await AdoptDurableImportsAsync();

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
                var transferKey = TransferKey(it);
                if (failed.Contains(transferKey)) continue; // dropped; excluded from the average

                if (!acquisitionBackends.TryGet(it.Protocol, out var backend))
                {
                    logger.LogWarning("Job {JobId}: {Protocol} backend unavailable for in-flight transfer {TransferId}; retaining it for recovery",
                        job.Id, it.Protocol, it.TransferId);
                    continue;
                }
                var status = await backend.GetStatusAsync(it.TransferId, ct);
                if (status is null)
                {
                    var firstMissing = missingSince.TryGetValue(transferKey, out var seen)
                        ? seen
                        : (missingSince[transferKey] = DateTime.UtcNow);
                    // Unknown audit state is not evidence of failure. When it is reachable, require the
                    // transfer to remain absent for more than one monitor cycle, then re-read once at the
                    // destructive boundary to close the last cleanup race window.
                    if (!auditAvailable || !MissingTransferGraceExpired(firstMissing, DateTime.UtcNow, missingGrace))
                        continue;
                    var finalAuditAvailable = await AdoptDurableImportsAsync();
                    if (!finalAuditAvailable) continue;
                    if (items[i].Imported)
                    {
                        progressSum += 100;
                        continue;
                    }
                    await FailTransferAsync(it,
                        $"A transfer remained absent from its acquisition backend for {missingGrace.TotalSeconds:F0}s",
                        BlocklistReason.DownloadFailed);
                    continue;
                }
                missingSince.Remove(transferKey);
                latest[(it.Protocol, it.TransferId)] = status;
                if (string.Equals(status.State, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    await FailTransferAsync(it, "A transfer entered an error state", BlocklistReason.DownloadFailed); continue;
                }

                var nowTick = DateTime.UtcNow;
                if (lastProgress.TryGetValue(transferKey, out var last) && status.Progress > last.Progress)
                    lastProgress[transferKey] = (status.Progress, nowTick);
                else if (lastProgress.TryGetValue(transferKey, out var stuck) && nowTick - stuck.ChangedAt > stallTimeout)
                {
                    await FailTransferAsync(it, $"Transfer stalled at {status.Progress:F0}% for over {stallTimeout.TotalMinutes:F0}m", BlocklistReason.Stalled);
                    continue;
                }

                // Pack trimmed to canonical episode targets: once the backend has resolved the file list,
                // deselect files we do not need. A single alternative-order pack may span several Plex
                // seasons, so matching only an integer episode inside it.Season is not sufficient.
                var canonicalTargets = CanonicalTargets(it);
                if (backend.Capabilities.SupportsFileSelection &&
                    it.IsPack && canonicalTargets.Count > 0 &&
                    !trimmed.Contains(transferKey) && status.Files.Count > 0)
                {
                    trimmed.Add(transferKey);
                    var selection = BuildCanonicalPackFileSelection(job, it, status.Files, parser,
                        libraryPrefs.Current.VideoExtensions, libraryPrefs.Current.SubtitleExtensions);
                    var keep = selection.Keep;
                    // A physical file may cover several episodes, so count the declared logical coverage,
                    // not selected files. When the union cannot prove every wanted episode, leave all files
                    // selected for inspection but retain the canonical targets; the organizer will reject the pack
                    // rather than silently relaxing the request (the old kids-show numbering failure mode).
                    var missingCoverage = selection.MissingCoverage;
                    if (missingCoverage.Count > 0)
                    {
                        logger.LogWarning("Job {JobId} torrent {TorrentId}: file identities cannot prove requested episode(s) {Missing} — keeping all files for inspection without relaxing the import contract",
                            job.Id, it.TransferId, DescribeTargets(missingCoverage));
                    }
                    else if (keep.Any(k => !k) && await backend.SetWantedFilesAsync(it.TransferId, keep, ct))
                        logger.LogInformation("Job {JobId} torrent {TorrentId}: pack trimmed to canonical episode(s) {Needed} — downloading {Kept}/{Total} file(s)",
                            job.Id, it.TransferId, DescribeTargets(canonicalTargets), keep.Count(k => k), keep.Count);
                }

                progressSum += status.Progress;

                if (status.IsFinished || status.Progress >= 100)
                {
                    var sourcePath = ImportSourceResolver.Resolve(status, job.Id, it.TransferId, logger);
                    if (sourcePath is null)
                    {
                        // is_finished can lead the actual flush-to-disk; give the files a grace window to
                        // appear before treating an unresolvable path as a real failure.
                        var firstFinished = finishedSince.TryGetValue(transferKey, out var t) ? t : (finishedSince[transferKey] = nowTick);
                        if (nowTick - firstFinished < finishSettle)
                        {
                            logger.LogDebug("Job {JobId} torrent {TorrentId}: finished but on-disk path not resolvable yet; re-checking (waited {Elapsed:F0}s of {Grace:F0}s grace)",
                                job.Id, it.TransferId, (nowTick - firstFinished).TotalSeconds, finishSettle.TotalSeconds);
                            continue;
                        }
                        await FailTransferAsync(it, $"Could not resolve an on-disk path for the finished transfer after {finishSettle.TotalSeconds:F0}s (save_path={status.SavePath}, reported name=\"{status.Name}\")", BlocklistReason.PathUnresolvable);
                        continue;
                    }
                    // Surface the "renaming & moving" phase in the admin panel before the (potentially slow,
                    // blocking) import so it doesn't look stuck at 100% while files are being transferred.
                    await SafeReportProgress(job.Id, (int)Math.Round(progressSum / Math.Max(1, items.Count)), BuildTelemetry(importingId: transferKey));
                    var result = await importCoordinator.RunOnceAsync(it.Protocol, it.TransferId,
                        token => importer.ImportAsync(job, it, sourcePath, token), ct);
                    if (!result.Success)
                    {
                        await FailTransferAsync(it, result.FailReason ?? "A download completed but import failed",
                            result.BlocklistReason ?? BlocklistReason.ImportFailed);
                        continue;
                    }

                    try
                    {
                        var files = result.Files.Select(f => new ImportedFileDto
                        {
                            TransferId = it.TransferId,
                            Protocol = it.Protocol,
                            SourcePath = f.SourcePath,
                            DestinationPath = f.DestinationPath,
                            FileType = f.FileType,
                            SeasonNumber = f.Season,
                            EpisodeNumber = f.Episode,
                            SizeBytes = f.SizeBytes,
                            ResolutionHeight = it.Resolution, // resolution the ranker chose for this torrent
                            ReleaseName = it.ReleaseName,
                            SourceId = it.SourceId,
                            MediaTracks = f.MediaTracks,
                            EpisodeCoverage = f.EpisodeCoverage?.ToList() ?? new()
                        }).ToList();
                        var auditSaved = await api.ReportImportedFilesAsync(job.Id, files, ct);
                        if (!auditSaved && job.IsReplacement)
                        {
                            logger.LogWarning("Job {JobId}: replacement import is on disk but its audit row was not accepted; retaining transfer and retrying before old-file cleanup", job.Id);
                            continue;
                        }
                        foreach (var f in files) importedDestinations.Add(f.DestinationPath);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not persist import audit rows for job {JobId}", job.Id);
                        // Replacement cleanup is driven by that audit trail. Never advance to deletion while
                        // the web API cannot prove which new episode actually landed; the next monitor tick
                        // reuses the idempotent import result and retries this write.
                        if (job.IsReplacement) continue;
                    }

                    try { await api.RefreshLibraryAsync(job.MediaType, ct); }
                    catch (Exception ex) { logger.LogDebug(ex, "Plex library refresh trigger skipped for job {JobId}", job.Id); }

                    progressSum += 100 - status.Progress; // count the just-imported torrent as fully done this tick
                    items[i] = it with { Imported = true };
                    await stateStore.SaveAsync(record with { Transfers = items.ToList() }, ct); // persist so a restart resumes
                    var cleanupCompleted = await postImportCleanup.RunAsync(it.Protocol, it.TransferId, result, ct);
                    await SafeReportCleanup(job.Id, it, cleanupCompleted,
                        cleanupCompleted ? null : "Backend cleanup was deferred and will be retried");
                }
            }

            await SafeReportProgress(job.Id, (int)Math.Round(progressSum / Math.Max(1, items.Count)), BuildTelemetry());

            // Terminal when every torrent has either imported or failed — no early abort on a single failure.
            if (items.All(x => x.Imported || failed.Contains(TransferKey(x))))
            {
                // A complete replacement needs the lifetime audit (including earlier partial attempts)
                // before old paths can be cleaned. Keep the monitor alive during a transient API outage.
                if (job.IsReplacement && items.All(x => x.Imported) && !await AdoptDurableImportsAsync())
                {
                    logger.LogWarning("Job {JobId}: all replacements imported but the durable audit is unavailable; retaining old paths and retrying", job.Id);
                }
                else break;
            }

            await Task.Delay(interval, ct);
        }

        await AdoptDurableImportsAsync();
        var importedCount = items.Count(x => x.Imported);

        if (job.IsUpgrade)
        {
            // Upgrade/replacement job: keep the old file until a new import succeeds. Routine upgrades stop
            // quietly when exhausted; issue replacements defer and search again because the old file is known bad.
            if (importedCount > 0 && (!job.IsReplacement || ReplacementReadyToFinalize(
                    record.CoversAllTargets, importedCount, items.Count)))
            {
                DeleteReplacedFiles(job, importedDestinations);
                await SafeMarkUpgraded(job.Id);
            }
            else if (job.IsReplacement)
            {
                var detail = importedCount > 0
                    ? $"{importedCount}/{items.Count} replacement downloads imported; keeping the replacement open until every requested target is covered"
                    : failReasons.Count > 0
                        ? $"Replacement downloads failed: {string.Join("; ", failReasons.Distinct())}"
                        : "No replacement imported; searching again later";
                await SafeDefer(job.Id, detail);
            }
            else
            {
                await SafeMarkUpgradeExhausted(job.Id);
            }
        }
        else if (importedCount == items.Count && record.CoversAllTargets)
        {
            if (job.MediaType != MediaType.Music || await WaitForPlexVerificationAsync(job, ct))
                await SafeMarkFulfilled(job.MediaRequestId);
            else
                logger.LogWarning("Job {JobId} \"{Title}\": files are imported but Plex has not indexed them yet; server reconciliation will keep checking and no Available notification was sent",
                    job.Id, job.Title);
        }
        else if (importedCount > 0)
        {
            var reason = !record.CoversAllTargets && importedCount == items.Count
                ? $"{importedCount} available download(s) imported, but no release covered every requested season/episode"
                : $"{importedCount}/{items.Count} downloads imported; the rest failed: {string.Join("; ", failReasons.Distinct())}";
            // Continuation makes this same job claimable again. Delete the old planner state first so a
            // crash or a second worker can never resume the already-imported transfer list.
            await SafeRemoveState(job.Id);
            await SafePartiallyComplete(job.MediaRequestId, reason);
            return;
        }
        else
        {
            // No hard "Failed" for a first-time grab either: park it and keep searching on a backoff.
            var reason = string.Join("; ", failReasons.Distinct()) is { Length: > 0 } r ? r : "All downloads failed";
            await SafeDefer(job.Id, reason);
        }
        await SafeRemoveState(job.Id);
    }

    private async Task<bool> WaitForPlexVerificationAsync(FulfillmentJobDto job, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMinutes(Math.Clamp(worker.Value.PlexVerificationTimeoutMinutes, 1, 60));
        var deadline = DateTime.UtcNow + timeout;
        var nextRefresh = DateTime.MinValue;
        string? lastDetail = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= nextRefresh)
            {
                await api.RefreshLibraryAsync(MediaType.Music, ct);
                nextRefresh = DateTime.UtcNow.AddMinutes(1);
            }
            var result = await api.VerifyLibraryAsync(job, ct);
            lastDetail = result.Detail;
            if (result.Available)
            {
                logger.LogInformation("Job {JobId} \"{Title}\": Plex verified music import ({Detail}, ratingKey={RatingKey})",
                    job.Id, job.Title, result.Detail, result.RatingKey);
                return true;
            }
            await SafeReportProgress(job.Id, 100);
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromSeconds(10) ? remaining : TimeSpan.FromSeconds(10), ct);
        }
        logger.LogWarning("Job {JobId} \"{Title}\": Plex verification timed out after {Minutes}m ({Detail})",
            job.Id, job.Title, timeout.TotalMinutes, lastDetail ?? "not indexed");
        return false;
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
    private async Task SafeReportProgress(int jobId, int progress, IReadOnlyList<DownloadTransferTelemetry>? transfers = null)
    {
        try { await api.ReportProgressAsync(jobId, progress, transfers, CancellationToken.None); }
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
    internal static DownloadPlan BuildForcedPlan(FulfillmentJobDto job)
    {
        var episode = job.RequestedEpisodes.FirstOrDefault();
        var canonicalTargets = job.SeasonTargets
            .SelectMany(target => target.MissingEpisodes.Select(number =>
                new EpisodeRef { Season = target.Season, Episode = number }))
            .DistinctBy(target => (target.Season, target.Episode))
            .OrderBy(target => target.Season).ThenBy(target => target.Episode)
            .ToList();
        var requestedSeasons = canonicalTargets.Select(target => target.Season)
            .Concat(job.RequestedSeasons)
            .Distinct().OrderBy(seasonNumber => seasonNumber).ToList();
        var season = episode?.Season ?? (requestedSeasons.Count == 1 ? requestedSeasons[0] : null);
        bool isPack = episode is null;

        var candidate = new ReleaseCandidate
        {
            ReleaseName = job.ForcedReleaseName ?? job.Title,
            Acquisition = AcquisitionResource.Torrent(job.ForcedMagnet!, MagnetUtil.InfoHashFromMagnet(job.ForcedMagnet)),
            IndexerId = job.ForcedIndexerId ?? 0,
            Source = "manual"
        };

        var item = new DownloadPlanItem(candidate, season == 0 ? null : season, episode?.Episode, isPack)
        {
            NeededEpisodes = isPack && requestedSeasons.Count == 1 && canonicalTargets.Count > 0
                ? canonicalTargets.Select(target => target.Episode).ToList()
                : null,
            NeededEpisodeRefs = isPack && canonicalTargets.Count > 0 ? canonicalTargets : null,
            RequiresManifestPreflight = job.IsAnime && isPack && canonicalTargets.Count > 0
        };
        return new DownloadPlan(isPack ? DownloadPlanKind.SeasonPack : DownloadPlanKind.Episodes, new[] { item });
    }

    /// <summary>Inspect a bounded number of otherwise-acceptable anime collections without starting their
    /// payload. A TMDb order is adopted only after one full mapping contract uniquely passes the same
    /// manifest preflight enforced again immediately before enqueue.</summary>
    private async Task<(DownloadPlan Plan, string? Detail)> TryResolveAnimeEpisodeOrderAsync(
        FulfillmentJobDto job, CancellationToken ct)
    {
        var canonicalTargets = job.RequestedEpisodes
            .Concat(job.SeasonTargets.SelectMany(target => target.MissingEpisodes.Select(episode =>
                new EpisodeRef { Season = target.Season, Episode = episode })))
            .Where(target => target.Season >= 0 && target.Episode > 0)
            .DistinctBy(target => (target.Season, target.Episode))
            .OrderBy(target => target.Season).ThenBy(target => target.Episode)
            .ToList();
        if (canonicalTargets.Count == 0)
            return (DownloadPlan.None,
                "Anime episode-order discovery needs an exact canonical episode target set before it can inspect a collection");

        var failures = new List<string>();
        var maxPackGb = job.QualityProfile?.MaxSeasonPackSizeGb ?? prefs.Current.MaxSeasonPackSizeGb;
        foreach (var candidate in ranker.ManifestFallbackCandidates
                     .DistinctBy(candidate => candidate.Acquisition.SourceId ?? candidate.Acquisition.Locator,
                         StringComparer.OrdinalIgnoreCase)
                     .Take(3))
        {
            var resource = candidate.Acquisition;
            if (!acquisitionBackends.TryGet(resource.Protocol, out var backend)
                || !backend.Capabilities.SupportsManifestPreflight)
            {
                failures.Add($"{candidate.ReleaseName}: {resource.Protocol} cannot inspect manifests");
                continue;
            }

            AcquisitionManifest? manifest;
            try
            {
                manifest = await backend.GetManifestAsync(resource, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{candidate.ReleaseName}: metadata inspection failed ({ex.Message})");
                continue;
            }
            if (manifest is null)
            {
                failures.Add($"{candidate.ReleaseName}: metadata did not arrive within the preflight timeout");
                continue;
            }

            var item = new DownloadPlanItem(candidate, null, null, true)
            {
                NeededEpisodeRefs = canonicalTargets,
                RequiresManifestPreflight = true
            };

            var namedPlan = TryPlanNamedAnimeCollection(manifest, job, item, parser,
                libraryPrefs.Current.VideoExtensions, maxPackGb, libraryPrefs.Current.SubtitleExtensions);
            if (namedPlan is not null)
            {
                var selectedTargets = namedPlan.Value.Plan.Items[0].NeededEpisodeRefs?.Count ?? 0;
                var namedDetail = namedPlan.Value.Plan.CoversAllTargets
                    ? "Manifest uniquely matched its named folders to every frozen canonical target. "
                      + namedPlan.Value.Decision.Detail
                    : $"Manifest uniquely matched named folders for {selectedTargets} of {canonicalTargets.Count} " +
                      $"canonical targets; the remaining seasons will continue separately. {namedPlan.Value.Decision.Detail}";
                logger.LogInformation("Job {JobId}: accepted named anime collection — {Detail}", job.Id, namedDetail);
                return (namedPlan.Value.Plan, namedDetail);
            }

            var namedDecision = AnimeManifestPreflight.EvaluateWithEpisodeOrder(manifest, job, item, parser,
                libraryPrefs.Current.VideoExtensions, maxPackGb, episodeOrderProfile: null,
                subtitleExtensions: libraryPrefs.Current.SubtitleExtensions);

            var resolution = AnimeEpisodeOrderResolver.Resolve(manifest, job, item,
                job.EpisodeOrderCandidates, parser, libraryPrefs.Current.VideoExtensions, maxPackGb);
            if (!resolution.Resolved || resolution.Profile?.SourceEpisodeGroupId is not { Length: > 0 } groupId)
            {
                var structuralDetail = $"Named canonical folders: {namedDecision.Detail} | {resolution.Detail}";
                failures.Add($"{candidate.ReleaseName}: {structuralDetail}");
                await SafeBlocklist(job.Id, new BlocklistRequestDto
                {
                    InfoHash = resource.SourceId ?? MagnetUtil.InfoHashFromMagnet(resource.Locator),
                    Protocol = resource.Protocol,
                    SourceId = resource.SourceId,
                    ReleaseName = candidate.ReleaseName,
                    Reason = BlocklistReason.EpisodeMappingAmbiguous,
                    Detail = structuralDetail,
                    IndexerId = candidate.IndexerId > 0 ? candidate.IndexerId : null
                });
                continue;
            }

            var persisted = await api.ApplyEpisodeOrderGroupAsync(job.Id, groupId, ct);
            if (persisted is null)
                return (DownloadPlan.None,
                    $"{candidate.ReleaseName}: a unique manifest match was found, but its authoritative TMDb order could not be frozen onto the job; it will retry safely");

            job.EpisodeOrderProfile = persisted;
            var plan = ranker.PlanDownload([candidate], job);
            if (plan.IsEmpty)
                return (DownloadPlan.None,
                    $"{candidate.ReleaseName}: TMDb order \"{persisted.SourceEpisodeGroupName}\" was saved, but the release no longer formed a complete plan");

            logger.LogInformation("Job {JobId}: automatically selected anime episode order — {Detail}",
                job.Id, resolution.Detail);
            return (plan, resolution.Detail);
        }

        var detail = failures.Count == 0
            ? "No safe anime collection was available for episode-order discovery"
            : $"Anime collection metadata could not prove one episode order: {string.Join("; ", failures.Take(3))}";
        return (DownloadPlan.None, detail);
    }

    /// <summary>Build a full or partial plan from uniquely named canonical season folders. Each canonical
    /// season must independently prove all of its current targets; a final combined pass then repeats the
    /// exact coverage, overlap, and selected-byte checks. This lets a collection safely satisfy the seasons
    /// it can prove while leaving an ambiguous arc for a later release.</summary>
    internal static (DownloadPlan Plan, ManifestPreflightDecision Decision)? TryPlanNamedAnimeCollection(
        AcquisitionManifest manifest,
        FulfillmentJobDto job,
        DownloadPlanItem item,
        IReleaseParser parser,
        IReadOnlyCollection<string> videoExtensions,
        double maxSelectedGb,
        IReadOnlyCollection<string> subtitleExtensions)
    {
        if (EpisodeOrderMapping.IsActive(job.EpisodeOrderProfile)
            || item.NeededEpisodeRefs is not { Count: > 0 }
            || job.CanonicalSeasons.Count == 0)
            return null;

        var allDecision = AnimeManifestPreflight.EvaluateWithEpisodeOrder(manifest, job, item, parser,
            videoExtensions, maxSelectedGb, episodeOrderProfile: null, subtitleExtensions: subtitleExtensions);
        if (allDecision.Accepted)
            return (new DownloadPlan(DownloadPlanKind.Single, [item]), allDecision);

        var safeTargets = new List<EpisodeRef>();
        foreach (var seasonTargets in item.NeededEpisodeRefs
                     .GroupBy(target => target.Season)
                     .OrderBy(group => group.Key))
        {
            var targets = seasonTargets.OrderBy(target => target.Episode).ToList();
            var seasonItem = item with { NeededEpisodeRefs = targets };
            var seasonDecision = AnimeManifestPreflight.EvaluateWithEpisodeOrder(manifest, job, seasonItem,
                parser, videoExtensions, maxSelectedGb, episodeOrderProfile: null,
                subtitleExtensions: subtitleExtensions);
            if (seasonDecision.Accepted) safeTargets.AddRange(targets);
        }
        if (safeTargets.Count == 0) return null;

        var partialItem = item with { NeededEpisodeRefs = safeTargets };
        var combinedDecision = AnimeManifestPreflight.EvaluateWithEpisodeOrder(manifest, job, partialItem,
            parser, videoExtensions, maxSelectedGb, episodeOrderProfile: null,
            subtitleExtensions: subtitleExtensions);
        if (!combinedDecision.Accepted) return null;

        return (new DownloadPlan(DownloadPlanKind.Single, [partialItem],
            CoversAllTargets: safeTargets.Count == item.NeededEpisodeRefs.Count), combinedDecision);
    }

    private static string TransferKey(TransferItem transfer) => TransferKey(transfer.Protocol, transfer.TransferId);

    private static string TransferKey(AcquisitionProtocol protocol, string transferId) =>
        $"{(int)protocol}:{transferId}";

    internal static HashSet<(int Season, int Episode)> CanonicalTargets(TransferItem transfer)
    {
        if (transfer.NeededEpisodeRefs is { Count: > 0 })
            return transfer.NeededEpisodeRefs.Where(x => x.Season >= 0 && x.Episode > 0)
                .Select(x => (x.Season, x.Episode)).ToHashSet();
        if (transfer.Season is int season && transfer.NeededEpisodes is { Count: > 0 })
            return transfer.NeededEpisodes.Where(x => x > 0).Select(x => (season, x)).ToHashSet();
        return transfer.Season is int singleSeason && transfer.Episode is int singleEpisode && singleEpisode > 0
            ? [(singleSeason, singleEpisode)]
            : [];
    }

    /// <summary>Re-derive a pack's wanted-file priorities after the backend exposes its live file list.
    /// Only explicitly mapped target videos and recognized subtitle sidecars remain selected. In particular,
    /// an unnumbered NCOP/NCED/sample is a video extra, not a harmless companion file; re-enabling it here
    /// would override the payload-free manifest decision and make the organizer reject the completed pack.</summary>
    internal static CanonicalPackFileSelection BuildCanonicalPackFileSelection(
        FulfillmentJobDto job,
        TransferItem transfer,
        IReadOnlyList<string> files,
        IReleaseParser parser,
        IReadOnlyCollection<string> videoExtensions,
        IReadOnlyCollection<string> subtitleExtensions)
    {
        var targets = CanonicalTargets(transfer);
        var declaredCoverage = new HashSet<(int Season, int Episode)>();
        var videos = videoExtensions.Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.StartsWith('.') ? extension : $".{extension}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var subtitles = subtitleExtensions.Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.StartsWith('.') ? extension : $".{extension}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keep = files.Select(file =>
        {
            var extension = Path.GetExtension(file);
            if (subtitles.Contains(extension)) return true;
            if (!videos.Contains(extension)) return false;

            var parsed = parser.Parse(Path.GetFileName(file));
            var canonical = new List<(int Season, int Episode)>();
            if (transfer.FractionalEpisodeInsertionAfter is int insertionAfter
                && transfer.SourceSeason is int sequenceSource
                && transfer.Season is int sequenceSeason
                && AnimeManifestPreflight.CanonicalEpisodeCount(job, sequenceSeason) is int sequenceCount
                && AnimeNamedSeasonSequenceMapper.TryMapFile(file, job, sequenceSource, sequenceSeason,
                    sequenceCount, insertionAfter, parser, out var sequenceEpisode))
                canonical.Add((sequenceSeason, sequenceEpisode));
            else if (parsed.EpisodeNumbers.Count == 0)
                return false;
            else if (EpisodeOrderMapping.IsActive(job.EpisodeOrderProfile) && parsed.Season is int sourceSeason)
            {
                foreach (var sourceEpisode in parsed.EpisodeNumbers)
                    if (EpisodeOrderMapping.TryTranslateFile(job.EpisodeOrderProfile, file,
                            sourceSeason, sourceEpisode, out var target))
                        canonical.Add((target.Season, target.Episode));
            }
            else if (transfer.Season is null && job.CanonicalSeasons.Count > 0)
            {
                if (AnimeNamedCollectionMapper.TryMapFile(file, job, parsed, out var namedCoverage))
                    canonical.AddRange(namedCoverage.Select(target => (target.Season, target.Episode)));
            }
            else if (transfer.SourceSeason is int expectedSource
                     && transfer.Season is int remappedSeason
                     && expectedSource != remappedSeason)
            {
                var namedAbsoluteEpisode = parsed.Season == 0
                    && AnimeManifestPreflight.MatchesNamedCanonicalSeason(job, file,
                        expectedSource, remappedSeason);
                if (parsed.Season == expectedSource || namedAbsoluteEpisode)
                    canonical.AddRange(parsed.EpisodeNumbers.Select(episode => (remappedSeason, episode)));
            }
            else if ((parsed.Season ?? transfer.Season) is int canonicalSeason)
                canonical.AddRange(parsed.EpisodeNumbers.Select(episode => (canonicalSeason, episode)));
            var selected = canonical.Count > 0 && canonical.All(targets.Contains);
            if (selected) declaredCoverage.UnionWith(canonical);
            return selected;
        }).ToList();
        var missing = targets.Where(target => !declaredCoverage.Contains(target))
            .OrderBy(target => target.Season).ThenBy(target => target.Episode).ToList();
        return new CanonicalPackFileSelection(keep, missing);
    }

    private static string DescribeTargets(IEnumerable<(int Season, int Episode)> targets) =>
        string.Join(",", targets.OrderBy(x => x.Season).ThenBy(x => x.Episode)
            .Select(x => $"S{x.Season:D2}E{x.Episode:D2}"));

    internal static long EstimatedPayloadBytes(PreparedDownloadItem prepared, MediaType mediaType,
        StorageOptions options)
    {
        if (prepared.Manifest is { Files.Count: > 0 } manifest)
        {
            var selected = prepared.WantedFiles is { Count: > 0 } wanted
                ? manifest.Files.Where((_, index) => index < wanted.Count && wanted[index])
                : manifest.Files;
            var exact = selected.Sum(x => Math.Max(0, x.SizeBytes));
            if (exact > 0) return exact;
        }
        if (prepared.Item.Candidate.SizeKnown && prepared.Item.Candidate.SizeBytes > 0)
            return prepared.Item.Candidate.SizeBytes;
        var fallbackGb = mediaType == MediaType.Music
            ? options.UnknownMusicSizeGb
            : options.UnknownVideoSizeGb;
        return (long)(Math.Clamp(fallbackGb, 0.1, 1000) * 1024 * 1024 * 1024);
    }

    internal static bool ReplacementReadyToFinalize(bool coversAllTargets, int importedCount, int transferCount) =>
        coversAllTargets && transferCount > 0 && importedCount == transferCount;

    internal static bool MissingTransferGraceExpired(DateTime firstSeen, DateTime now, TimeSpan grace) =>
        now - firstSeen >= grace;

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

    private async Task SafeReportCleanup(int jobId, TransferItem transfer, bool completed, string? error)
    {
        try
        {
            await api.ReportTransferCleanupAsync(new TransferCleanupReportDto(
                jobId, transfer.Protocol, transfer.TransferId, completed, error), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not persist cleanup result for transfer {TransferId}; maintenance will retry",
                transfer.TransferId);
        }
    }
}
