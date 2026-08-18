using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

public interface IFulfillmentTorrentService
{
    /// <summary>Record the torrents backing a job. Idempotent by (job, torrent id) so a retry or a
    /// re-adopted duplicate updates the existing row instead of creating a second one.</summary>
    Task<int> RegisterAsync(int jobId, IReadOnlyList<TrackedTorrentDto> torrents);

    /// <summary>Everything the database believes is still in flight — the reconciler's left-hand side.</summary>
    Task<List<TrackedTorrentDto>> GetActiveAsync();

    /// <summary>Apply a reconciliation pass. Returns how many rows changed.</summary>
    Task<int> ApplyAsync(IReadOnlyList<TorrentStateUpdateDto> updates);

    /// <summary>Per-job torrents for the admin panel, newest job first.</summary>
    Task<List<TrackedTorrentDto>> GetForJobAsync(int jobId);

    /// <summary>One-time repair for rows written off as Missing that had in fact been imported. Idempotent.</summary>
    Task<int> CorrectMisclassifiedMissingAsync();
}

/// <summary>
/// Owns the durable job↔torrent link.
///
/// Before this existed the link lived only in the downloader's local JSON file and an in-memory telemetry
/// store, so it did not survive the worker being replaced and could never be queried. The consequence was
/// visible on the live deployment: a job marked "Deferred — no acceptable release found yet" while the 67
/// torrents it had added were still in Deluge, nine of them finished and none imported, with the job
/// re-searching on a backoff and re-adding the same magnets forever.
/// </summary>
public class FulfillmentTorrentService(AppDbContext db, ILogger<FulfillmentTorrentService> logger)
    : IFulfillmentTorrentService
{
    public async Task<int> RegisterAsync(int jobId, IReadOnlyList<TrackedTorrentDto> torrents)
    {
        if (torrents.Count == 0) return 0;

        var ids = torrents.Select(t => t.TorrentId).ToList();
        var existing = await db.FulfillmentTorrents
            .Where(t => t.FulfillmentJobId == jobId && ids.Contains(t.TorrentId))
            .ToDictionaryAsync(t => t.TorrentId, StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var t in torrents)
        {
            if (string.IsNullOrWhiteSpace(t.TorrentId)) continue;

            if (existing.TryGetValue(t.TorrentId, out var row))
            {
                // Re-registering an already-known torrent is normal (a duplicate add that adopted the
                // existing one). Refresh what we know; never resurrect a terminal state here — the
                // reconciler owns state transitions.
                row.ReleaseName ??= t.ReleaseName;
                row.InfoHash ??= t.InfoHash;
                row.Source ??= Trim(t.Source, 128);
                row.IndexerId ??= t.IndexerId;
                continue;
            }

            db.FulfillmentTorrents.Add(new FulfillmentTorrentEntity
            {
                FulfillmentJobId = jobId,
                TorrentId = t.TorrentId,
                InfoHash = t.InfoHash,
                ReleaseName = t.ReleaseName,
                Source = Trim(t.Source, 128),
                IndexerId = t.IndexerId,
                Season = t.Season,
                Episode = t.Episode,
                IsPack = t.IsPack,
                NeededEpisodesCsv = t.NeededEpisodes is { Count: > 0 } n ? string.Join(",", n) : null,
                Resolution = t.Resolution,
                State = TorrentTrackingState.Active,
                AddedAt = DateTime.UtcNow
            });
            added++;
        }

        await db.SaveChangesAsync();
        if (added > 0) logger.LogInformation("Job {JobId}: tracking {Count} torrent(s)", jobId, added);
        return added;
    }

    public async Task<List<TrackedTorrentDto>> GetActiveAsync()
    {
        var active = await db.FulfillmentTorrents.AsNoTracking()
            .Where(t => t.State == TorrentTrackingState.Active || t.State == TorrentTrackingState.Finished)
            .OrderBy(t => t.Id)
            .Select(t => ToDto(t))
            .ToListAsync();

        // One physical torrent may back several jobs, but it must be observed/imported only once per pass.
        // Choose the newest mapping so a current retry/upgrade supplies the import context rather than a
        // stale historical job; ApplyAsync still fans the resulting state out to every mapping.
        return active
            .GroupBy(t => t.TorrentId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(t => t.Id).First())
            .OrderBy(t => t.Id)
            .ToList();
    }

    public async Task<List<TrackedTorrentDto>> GetForJobAsync(int jobId) =>
        await db.FulfillmentTorrents.AsNoTracking()
            .Where(t => t.FulfillmentJobId == jobId)
            .OrderBy(t => t.Season).ThenBy(t => t.Episode).ThenBy(t => t.Id)
            .Select(t => ToDto(t))
            .ToListAsync();

    public async Task<int> ApplyAsync(IReadOnlyList<TorrentStateUpdateDto> updates)
    {
        if (updates.Count == 0) return 0;

        var ids = updates.Select(u => u.TorrentId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var rows = await db.FulfillmentTorrents.Where(t => ids.Contains(t.TorrentId)).ToListAsync();
        // A physical torrent can legitimately back more than one fulfillment job. The database therefore
        // guarantees uniqueness by (job, torrent), not by torrent alone. Keep every job mapping so one
        // Deluge update advances them all instead of throwing while building a one-row dictionary.
        var byId = rows
            .GroupBy(r => r.TorrentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var changed = 0;

        foreach (var u in updates)
        {
            if (!byId.TryGetValue(u.TorrentId, out var matchingRows)) continue;

            var state = u.State;
            if (state == TorrentTrackingState.Missing && await WasImportedAsync(u.TorrentId))
            {
                state = TorrentTrackingState.Imported;
                logger.LogDebug("Torrent {Torrent} is gone from the client but its files were imported — recording Imported, not Missing",
                    u.TorrentId);
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
                var reason = state == TorrentTrackingState.Imported ? null : u.Reason;

                if (state != row.State)
                {
                    row.State = state;
                    if (state == TorrentTrackingState.Imported) row.ImportedAt = now;
                    if (state is TorrentTrackingState.Failed or TorrentTrackingState.Missing)
                        row.FailReason = Trim(reason, 512);
                    logger.LogInformation("Torrent {Torrent} (job {JobId}) -> {State}{Reason}",
                        row.TorrentId[..Math.Min(12, row.TorrentId.Length)], row.FulfillmentJobId, state,
                        string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}");
                }
                changed++;
            }
        }

        await db.SaveChangesAsync();
        return changed;
    }

    /// <summary>Did this torrent's files reach the library? The import audit is the authority — it is
    /// written only after files are actually placed.</summary>
    private Task<bool> WasImportedAsync(string torrentId) =>
        db.ImportedFiles.AsNoTracking().AnyAsync(f => f.TorrentId == torrentId);

    public async Task<int> CorrectMisclassifiedMissingAsync()
    {
        // Repairs rows written off before the check above existed. On the live deployment that was 59 of
        // them: torrents the pipeline had imported and removed, which the reconciler then quite reasonably
        // observed were no longer in the client and recorded as lost.
        var missing = await db.FulfillmentTorrents
            .Where(t => t.State == TorrentTrackingState.Missing)
            .ToListAsync();
        if (missing.Count == 0) return 0;

        var ids = missing.Select(t => t.TorrentId).ToList();
        var importedIds = await db.ImportedFiles.AsNoTracking()
            .Where(f => f.TorrentId != null && ids.Contains(f.TorrentId))
            .Select(f => f.TorrentId!)
            .Distinct()
            .ToListAsync();
        if (importedIds.Count == 0) return 0;

        var set = importedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fixedUp = 0;
        foreach (var row in missing.Where(t => set.Contains(t.TorrentId)))
        {
            row.State = TorrentTrackingState.Imported;
            row.ImportedAt ??= row.LastSeenAt ?? DateTime.UtcNow;
            row.FailReason = null;
            fixedUp++;
        }

        if (fixedUp > 0)
        {
            await db.SaveChangesAsync();
            logger.LogInformation("Corrected {Count} torrent(s) recorded as Missing that had actually been imported", fixedUp);
        }
        return fixedUp;
    }

    private static string? Trim(string? s, int max) => s is { Length: > 0 } && s.Length > max ? s[..max] : s;

    private static TrackedTorrentDto ToDto(FulfillmentTorrentEntity t) => new()
    {
        Id = t.Id,
        FulfillmentJobId = t.FulfillmentJobId,
        TorrentId = t.TorrentId,
        InfoHash = t.InfoHash,
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
}
