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
                continue;
            }

            db.FulfillmentTorrents.Add(new FulfillmentTorrentEntity
            {
                FulfillmentJobId = jobId,
                TorrentId = t.TorrentId,
                InfoHash = t.InfoHash,
                ReleaseName = t.ReleaseName,
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

    public async Task<List<TrackedTorrentDto>> GetActiveAsync() =>
        await db.FulfillmentTorrents.AsNoTracking()
            .Where(t => t.State == TorrentTrackingState.Active || t.State == TorrentTrackingState.Finished)
            .OrderBy(t => t.Id)
            .Select(t => ToDto(t))
            .ToListAsync();

    public async Task<List<TrackedTorrentDto>> GetForJobAsync(int jobId) =>
        await db.FulfillmentTorrents.AsNoTracking()
            .Where(t => t.FulfillmentJobId == jobId)
            .OrderBy(t => t.Season).ThenBy(t => t.Episode).ThenBy(t => t.Id)
            .Select(t => ToDto(t))
            .ToListAsync();

    public async Task<int> ApplyAsync(IReadOnlyList<TorrentStateUpdateDto> updates)
    {
        if (updates.Count == 0) return 0;

        var ids = updates.Select(u => u.TorrentId).ToList();
        var rows = await db.FulfillmentTorrents.Where(t => ids.Contains(t.TorrentId)).ToListAsync();
        var byId = rows.ToDictionary(r => r.TorrentId, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var changed = 0;

        foreach (var u in updates)
        {
            if (!byId.TryGetValue(u.TorrentId, out var row)) continue;

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

            if (u.State != row.State)
            {
                row.State = u.State;
                if (u.State == TorrentTrackingState.Imported) row.ImportedAt = now;
                if (u.State is TorrentTrackingState.Failed or TorrentTrackingState.Missing)
                    row.FailReason = Trim(u.Reason, 512);
                logger.LogInformation("Torrent {Torrent} (job {JobId}) -> {State}{Reason}",
                    row.TorrentId[..Math.Min(12, row.TorrentId.Length)], row.FulfillmentJobId, u.State,
                    string.IsNullOrWhiteSpace(u.Reason) ? "" : $": {u.Reason}");
            }
            changed++;
        }

        await db.SaveChangesAsync();
        return changed;
    }

    private static string? Trim(string? s, int max) => s is { Length: > 0 } && s.Length > max ? s[..max] : s;

    private static TrackedTorrentDto ToDto(FulfillmentTorrentEntity t) => new()
    {
        Id = t.Id,
        FulfillmentJobId = t.FulfillmentJobId,
        TorrentId = t.TorrentId,
        InfoHash = t.InfoHash,
        ReleaseName = t.ReleaseName,
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
