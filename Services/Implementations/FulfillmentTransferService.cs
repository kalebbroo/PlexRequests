using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
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
        foreach (var t in transfers)
        {
            if (string.IsNullOrWhiteSpace(t.TransferId)) continue;

            if (existing.TryGetValue((t.Protocol, t.TransferId), out var row))
            {
                // Re-registering an already-known transfer is normal (a duplicate enqueue that adopted the
                // existing one). Refresh what we know; never resurrect a terminal state here — the
                // reconciler owns state transitions.
                row.ReleaseName ??= t.ReleaseName;
                row.SourceId ??= t.SourceId;
                row.Source ??= Trim(t.Source, 128);
                row.IndexerId ??= t.IndexerId;
                row.NeededEpisodeRefsJson ??= SerializeEpisodeRefs(t.NeededEpisodeRefs);
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
        if (added > 0) logger.LogInformation("Job {JobId}: tracking {Count} transfer(s)", jobId, added);
        return added;
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

            var state = u.State;
            if (state == TransferTrackingState.Missing && await WasImportedAsync(u.Protocol, u.TransferId))
            {
                state = TransferTrackingState.Imported;
                logger.LogDebug("Transfer {Transfer} is gone from its backend but its files were imported — recording Imported, not Missing",
                    u.TransferId);
            }

            foreach (var row in matchingRows)
            {
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

    /// <summary>Did this transfer's files reach the library? The import audit is the authority — it is
    /// written only after files are actually placed.</summary>
    private Task<bool> WasImportedAsync(AcquisitionProtocol protocol, string transferId) =>
        db.ImportedFiles.AsNoTracking().AnyAsync(f => f.Protocol == protocol && f.TransferId == transferId);

    public async Task<int> CorrectMisclassifiedMissingAsync()
    {
        // Repairs rows written off before the check above existed. On the live deployment that was 59 of
        // them: torrents the pipeline had imported and removed, which the reconciler then quite reasonably
        // observed were no longer in the client and recorded as lost.
        var missing = await db.FulfillmentTransfers
            .Where(t => t.State == TransferTrackingState.Missing)
            .ToListAsync();
        if (missing.Count == 0) return 0;

        var ids = missing.Select(t => t.TransferId).Distinct().ToList();
        var importedKeys = await db.ImportedFiles.AsNoTracking()
            .Where(f => f.TransferId != null && ids.Contains(f.TransferId))
            .Select(f => new { f.Protocol, TransferId = f.TransferId! })
            .Distinct()
            .ToListAsync();
        if (importedKeys.Count == 0) return 0;

        var set = importedKeys.Select(x => (x.Protocol, x.TransferId)).ToHashSet(TransferKeyComparer.Instance);
        var fixedUp = 0;
        foreach (var row in missing.Where(t => set.Contains((t.Protocol, t.TransferId))))
        {
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
        FailReason = t.FailReason
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
