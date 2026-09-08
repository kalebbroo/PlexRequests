using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

public interface IFulfillmentTransferService
{
    /// <summary>Record the transfers backing a job. Idempotent by (job, protocol, backend id) so a retry or a
    /// re-adopted duplicate updates the existing row instead of creating a second one.</summary>
    Task<int> RegisterAsync(int jobId, IReadOnlyList<TrackedTransferDto> transfers);

    /// <summary>Everything the database believes is still in flight — the reconciler's left-hand side.</summary>
    Task<List<TrackedTransferDto>> GetActiveAsync();

    /// <summary>Apply a reconciliation pass. Returns how many rows changed.</summary>
    Task<int> ApplyAsync(IReadOnlyList<TransferStateUpdateDto> updates);

    /// <summary>Per-job transfers for the admin panel, newest job first.</summary>
    Task<List<TrackedTransferDto>> GetForJobAsync(int jobId);

    /// <summary>Imported transfers whose verified post-import backend cleanup has not completed yet.</summary>
    Task<List<TrackedTransferDto>> GetPendingCleanupAsync(int take = 100);

    /// <summary>Persist one cleanup attempt so a worker restart can retry instead of orphaning its payload.</summary>
    Task<bool> ReportCleanupAsync(TransferCleanupReportDto report);

    /// <summary>One-time repair for rows written off as Missing that had in fact been imported. Idempotent.</summary>
    Task<int> CorrectMisclassifiedMissingAsync();
}

/// <summary>
/// Owns the durable job↔transfer link.
///
/// Before this existed the link lived only in the downloader's local JSON file and an in-memory telemetry
/// store, so it did not survive the worker being replaced and could never be queried. The consequence was
/// visible on the live deployment: a job marked "Deferred — no acceptable release found yet" while the 67
/// torrents it had added were still in Deluge, nine of them finished and none imported, with the job
/// re-searching on a backoff and re-adding the same magnets forever.
/// </summary>
public class FulfillmentTransferService(AppDbContext db, ILogger<FulfillmentTransferService> logger)
    : IFulfillmentTransferService
{
    public async Task<int> RegisterAsync(int jobId, IReadOnlyList<TrackedTransferDto> transfers)
    {
        if (transfers.Count == 0) return 0;

        var ids = transfers.Select(t => t.TransferId).Distinct().ToList();
        var existing = (await db.FulfillmentTransfers
            .Where(t => t.FulfillmentJobId == jobId && ids.Contains(t.TransferId))
            .ToListAsync())
            .ToDictionary(t => (t.Protocol, t.TransferId), TransferKeyComparer.Instance);

        var added = 0;
        var reactivated = 0;
        foreach (var t in transfers)
        {
            if (string.IsNullOrWhiteSpace(t.TransferId)) continue;

            if (existing.TryGetValue((t.Protocol, t.TransferId), out var row))
            {
                // Re-registering an already-known transfer is normal (a duplicate enqueue that adopted the
                // existing one). A Failed/Missing row is different: RegisterAsync is called only after the
                // backend accepted a concrete transfer for the current attempt, so leaving that row terminal
                // makes the durable reconciler blind to a real download. This happens when a retry selects the
                // same content-addressed torrent. Imported remains terminal; re-downloading an audited import
                // needs a new replacement job rather than silently erasing history.
                row.ReleaseName ??= t.ReleaseName;
                row.SourceId ??= t.SourceId;
                row.Source ??= Trim(t.Source, 128);
                row.IndexerId ??= t.IndexerId;
                row.SourceSeason ??= t.SourceSeason;
                row.FractionalEpisodeInsertionAfter ??= t.FractionalEpisodeInsertionAfter;
                row.NeededEpisodeRefsJson ??= SerializeEpisodeRefs(t.NeededEpisodeRefs);
                if (row.State is TransferTrackingState.Failed or TransferTrackingState.Missing)
                {
                    var now = DateTime.UtcNow;
                    row.ReleaseName = t.ReleaseName ?? row.ReleaseName;
                    row.SourceId = t.SourceId ?? row.SourceId;
                    row.Source = Trim(t.Source, 128) ?? row.Source;
                    row.IndexerId = t.IndexerId ?? row.IndexerId;
                    row.Season = t.Season;
                    row.SourceSeason = t.SourceSeason;
                    row.FractionalEpisodeInsertionAfter = t.FractionalEpisodeInsertionAfter;
                    row.Episode = t.Episode;
                    row.IsPack = t.IsPack;
                    row.NeededEpisodesCsv = t.NeededEpisodes is { Count: > 0 } needed
                        ? string.Join(",", needed)
                        : null;
                    row.NeededEpisodeRefsJson = SerializeEpisodeRefs(t.NeededEpisodeRefs);
                    row.Resolution = t.Resolution;
                    row.State = TransferTrackingState.Active;
                    row.Progress = 0;
                    row.Seeds = 0;
                    row.Peers = 0;
                    row.DownloadRateBytesPerSec = 0;
                    row.TotalSizeBytes = 0;
                    row.ProgressChangedAt = now;
                    row.LastSeenAt = null;
                    row.TrackerStatus = null;
                    row.FailReason = null;
                    row.ImportedAt = null;
                    row.CleanupCompletedAt = null;
                    row.CleanupLastAttemptAt = null;
                    row.CleanupError = null;
                    row.AddedAt = now;
                    reactivated++;
                }
                continue;
            }

            db.FulfillmentTransfers.Add(new FulfillmentTransferEntity
            {
                FulfillmentJobId = jobId,
                TransferId = t.TransferId,
                Protocol = t.Protocol,
                SourceId = t.SourceId,
                ReleaseName = t.ReleaseName,
                Source = Trim(t.Source, 128),
                IndexerId = t.IndexerId,
                Season = t.Season,
                SourceSeason = t.SourceSeason,
                FractionalEpisodeInsertionAfter = t.FractionalEpisodeInsertionAfter,
                Episode = t.Episode,
                IsPack = t.IsPack,
                NeededEpisodesCsv = t.NeededEpisodes is { Count: > 0 } n ? string.Join(",", n) : null,
                NeededEpisodeRefsJson = SerializeEpisodeRefs(t.NeededEpisodeRefs),
                Resolution = t.Resolution,
                State = TransferTrackingState.Active,
                AddedAt = DateTime.UtcNow
            });
            added++;
        }

        await db.SaveChangesAsync();
        if (added > 0 || reactivated > 0)
            logger.LogInformation("Job {JobId}: tracking {Added} new and {Reactivated} retried transfer(s)",
                jobId, added, reactivated);
        return added + reactivated;
    }

    public async Task<List<TrackedTransferDto>> GetActiveAsync()
    {
        var active = await db.FulfillmentTransfers.AsNoTracking()
            .Where(t => t.State == TransferTrackingState.Active || t.State == TransferTrackingState.Finished)
            .OrderBy(t => t.Id)
            .Select(t => ToDto(t))
            .ToListAsync();

        // One physical transfer may back several jobs, but it must be observed/imported only once per pass.
        // Choose the newest mapping so a current retry/upgrade supplies the import context rather than a
        // stale historical job; ApplyAsync still fans the resulting state out to every mapping.
        return active
            .GroupBy(t => (t.Protocol, t.TransferId), TransferKeyComparer.Instance)
            .Select(g => g.OrderByDescending(t => t.Id).First())
            .OrderBy(t => t.Id)
            .ToList();
    }

    public async Task<List<TrackedTransferDto>> GetForJobAsync(int jobId) =>
        await db.FulfillmentTransfers.AsNoTracking()
            .Where(t => t.FulfillmentJobId == jobId)
            .OrderBy(t => t.Season).ThenBy(t => t.Episode).ThenBy(t => t.Id)
            .Select(t => ToDto(t))
            .ToListAsync();

    public async Task<List<TrackedTransferDto>> GetPendingCleanupAsync(int take = 100) =>
        await db.FulfillmentTransfers.AsNoTracking()
            .Where(t => t.State == TransferTrackingState.Imported && t.CleanupCompletedAt == null)
            .OrderBy(t => t.CleanupLastAttemptAt ?? t.ImportedAt ?? t.AddedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(t => ToDto(t))
            .ToListAsync();

    public async Task<bool> ReportCleanupAsync(TransferCleanupReportDto report)
    {
        if (string.IsNullOrWhiteSpace(report.TransferId)) return false;
        var rows = await db.FulfillmentTransfers
            .Where(t => t.FulfillmentJobId == report.FulfillmentJobId
                && t.Protocol == report.Protocol
                && t.TransferId == report.TransferId
                && t.State == TransferTrackingState.Imported)
            .ToListAsync();
        if (rows.Count == 0) return false;

        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            row.CleanupLastAttemptAt = now;
            row.CleanupCompletedAt = report.Completed ? now : null;
            row.CleanupError = report.Completed ? null : Trim(report.Error ?? "Cleanup did not complete", 512);
        }
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<int> ApplyAsync(IReadOnlyList<TransferStateUpdateDto> updates)
    {
        if (updates.Count == 0) return 0;

        var ids = updates.Select(u => u.TransferId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var rows = await db.FulfillmentTransfers.Where(t => ids.Contains(t.TransferId)).ToListAsync();
        // A physical transfer can legitimately back more than one fulfillment job. Keep every job mapping
        // so one backend update advances them all instead of throwing while building a one-row dictionary.
        var byId = rows
            .GroupBy(r => (r.Protocol, r.TransferId), TransferKeyComparer.Instance)
            .ToDictionary(g => g.Key, g => g.ToList(), TransferKeyComparer.Instance);
        var now = DateTime.UtcNow;
        var changed = 0;

        foreach (var u in updates)
        {
            if (!byId.TryGetValue((u.Protocol, u.TransferId), out var matchingRows)) continue;

            foreach (var row in matchingRows)
            {
                var state = u.State;
                if (state == TransferTrackingState.Missing && await WasImportedAsync(row))
                {
                    state = TransferTrackingState.Imported;
                    logger.LogDebug(
                        "Transfer {Transfer} for job {JobId} is gone from its backend, but its current target scope was imported — recording Imported, not Missing",
                        u.TransferId, row.FulfillmentJobId);
                }

                // ProgressChangedAt tracks when the number MOVED, not when we last looked — stall detection is
                // meaningless otherwise, since polling frequently would keep a dead torrent looking fresh.
                if (Math.Abs(u.Progress - row.Progress) > 0.01) row.ProgressChangedAt = now;

                row.Progress = u.Progress;
                row.Seeds = u.Seeds;
                row.Peers = u.Peers;
                row.DownloadRateBytesPerSec = u.DownloadRateBytesPerSec;
                if (u.TotalSizeBytes > 0) row.TotalSizeBytes = u.TotalSizeBytes;
                row.TrackerStatus = Trim(u.TrackerStatus, 512);
                row.LastSeenAt = now;

                // A promotion to Imported carries no failure reason — it isn't one.
                var reason = state == TransferTrackingState.Imported ? null : u.Reason;

                if (state != row.State)
                {
                    row.State = state;
                    if (state == TransferTrackingState.Imported) row.ImportedAt = now;
                    if (state is TransferTrackingState.Failed or TransferTrackingState.Missing)
                        row.FailReason = Trim(reason, 512);
                    logger.LogInformation("Transfer {Transfer} (job {JobId}) -> {State}{Reason}",
                        row.TransferId[..Math.Min(12, row.TransferId.Length)], row.FulfillmentJobId, state,
                        string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}");
                }
                changed++;
            }
        }

        await db.SaveChangesAsync();
        return changed;
    }

    /// <summary>Did this transfer's current target scope reach the library? The same content-addressed
    /// payload can be reused for another season/episode slice, so the backend id alone is insufficient.</summary>
    private async Task<bool> WasImportedAsync(FulfillmentTransferEntity transfer)
    {
        var files = await db.ImportedFiles.AsNoTracking().Include(file => file.EpisodeCoverage)
            .Where(file => file.Protocol == transfer.Protocol && file.TransferId == transfer.TransferId)
            .ToListAsync();
        var intent = ToDto(transfer);
        return ImportAuditCoverage.Covers(intent.Protocol, intent.TransferId, intent.NeededEpisodeRefs,
            intent.Season, intent.Episode, intent.NeededEpisodes, files.Select(file => new ImportedFileDto
            {
                TransferId = file.TransferId,
                Protocol = file.Protocol,
                FileType = file.FileType,
                SeasonNumber = file.SeasonNumber,
                EpisodeNumber = file.EpisodeNumber,
                EpisodeCoverage = file.EpisodeCoverage.Select(coverage => new EpisodeRef
                {
                    Season = coverage.SeasonNumber,
                    Episode = coverage.EpisodeNumber
                }).ToList()
            }));
    }

    public async Task<int> CorrectMisclassifiedMissingAsync()
    {
        // Repairs rows written off before the check above existed. On the live deployment that was 59 of
        // them: torrents the pipeline had imported and removed, which the reconciler then quite reasonably
        // observed were no longer in the client and recorded as lost.
        var missing = await db.FulfillmentTransfers
            .Where(t => t.State == TransferTrackingState.Missing)
            .ToListAsync();
        if (missing.Count == 0) return 0;

        var fixedUp = 0;
        foreach (var row in missing)
        {
            if (!await WasImportedAsync(row)) continue;
            row.State = TransferTrackingState.Imported;
            row.ImportedAt ??= row.LastSeenAt ?? DateTime.UtcNow;
            row.FailReason = null;
            fixedUp++;
        }

        if (fixedUp > 0)
        {
            await db.SaveChangesAsync();
            logger.LogInformation("Corrected {Count} transfer(s) recorded as Missing that had actually been imported", fixedUp);
        }
        return fixedUp;
    }

    private static string? Trim(string? s, int max) => s is { Length: > 0 } && s.Length > max ? s[..max] : s;

    private static TrackedTransferDto ToDto(FulfillmentTransferEntity t) => new()
    {
        Id = t.Id,
        FulfillmentJobId = t.FulfillmentJobId,
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
        NeededEpisodes = string.IsNullOrWhiteSpace(t.NeededEpisodesCsv)
            ? new()
            : t.NeededEpisodesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out var n) ? n : -1).Where(n => n >= 0).ToList(),
        NeededEpisodeRefs = DeserializeEpisodeRefs(t.NeededEpisodeRefsJson),
        Resolution = t.Resolution,
        State = t.State,
        Progress = t.Progress,
        Seeds = t.Seeds,
        Peers = t.Peers,
        DownloadRateBytesPerSec = t.DownloadRateBytesPerSec,
        TotalSizeBytes = t.TotalSizeBytes,
        AddedAt = t.AddedAt,
        ProgressChangedAt = t.ProgressChangedAt,
        TrackerStatus = t.TrackerStatus,
        FailReason = t.FailReason,
        CleanupCompletedAt = t.CleanupCompletedAt,
        CleanupLastAttemptAt = t.CleanupLastAttemptAt,
        CleanupError = t.CleanupError
    };

    private static string? SerializeEpisodeRefs(IReadOnlyList<EpisodeRef>? refs) =>
        refs is { Count: > 0 }
            ? JsonSerializer.Serialize(refs.DistinctBy(x => (x.Season, x.Episode))
                .OrderBy(x => x.Season).ThenBy(x => x.Episode))
            : null;

    private static List<EpisodeRef> DeserializeEpisodeRefs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return (JsonSerializer.Deserialize<List<EpisodeRef>>(json) ?? new())
                .Where(x => x.Season >= 0 && x.Episode > 0)
                .DistinctBy(x => (x.Season, x.Episode))
                .OrderBy(x => x.Season).ThenBy(x => x.Episode).ToList();
        }
        catch (JsonException) { return new(); }
    }

    private sealed class TransferKeyComparer : IEqualityComparer<(AcquisitionProtocol Protocol, string TransferId)>
    {
        public static TransferKeyComparer Instance { get; } = new();
        public bool Equals((AcquisitionProtocol Protocol, string TransferId) x,
            (AcquisitionProtocol Protocol, string TransferId) y) =>
            x.Protocol == y.Protocol && StringComparer.OrdinalIgnoreCase.Equals(x.TransferId, y.TransferId);
        public int GetHashCode((AcquisitionProtocol Protocol, string TransferId) obj) =>
            HashCode.Combine(obj.Protocol, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TransferId));
    }
}
