using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequestsHosted.Services.Implementations;

public interface IReleaseBlocklistService
{
    /// <summary>Record a release that failed for a job, so it's never grabbed for that request again.</summary>
    Task<bool> BlockAsync(int fulfillmentJobId, BlocklistRequestDto request);

    Task<List<BlocklistEntryDto>> ListAsync(int? mediaRequestId = null, int take = 200);
    Task<bool> UnblockAsync(int id);
    Task<int> ClearForRequestAsync(int mediaRequestId);

    /// <summary>Info hashes to exclude when searching for this request.</summary>
    Task<List<string>> HashesForRequestAsync(int mediaRequestId);

    /// <summary>Delete lapsed entries. Called by the cleanup job.</summary>
    Task<int> PruneExpiredAsync(CancellationToken ct = default);
}

/// <summary>
/// Remembers releases that failed so a retry doesn't pick the same broken torrent again.
///
/// Before this, "never dead-end" meant a request whose download failed would re-search, rank the identical
/// torrent top, and fail again on every backoff tick — busy work that looked like progress. Blocking is
/// scoped to the request by default because a release that failed for one title may be perfectly fine
/// elsewhere; only a deliberate admin block goes wider.
/// </summary>
public class ReleaseBlocklistService(AppDbContext db, ILogger<ReleaseBlocklistService> logger) : IReleaseBlocklistService
{
    public async Task<bool> BlockAsync(int fulfillmentJobId, BlocklistRequestDto request)
    {
        var job = await db.FulfillmentJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == fulfillmentJobId);
        if (job is null) return false;

        // Derive the hash when the caller only had a magnet, so a release is still identifiable either way.
        var hash = MagnetUtil.Normalize(request.InfoHash);
        var normalizedName = string.IsNullOrWhiteSpace(request.ReleaseName)
            ? null
            : ReleaseBlocklistEntity.Normalize(request.ReleaseName);

        if (hash is null && normalizedName is null)
        {
            logger.LogDebug("Blocklist entry skipped for job {JobId}: neither an info hash nor a release name", fulfillmentJobId);
            return false;
        }

        // The unique (InfoHash, MediaRequestId) index means re-failing the same torrent updates rather than
        // piling up rows.
        var existing = await db.ReleaseBlocklist.FirstOrDefaultAsync(b =>
            b.MediaRequestId == job.MediaRequestId &&
            ((hash != null && b.InfoHash == hash) || (hash == null && b.NormalizedReleaseName == normalizedName)));

        if (existing is not null)
        {
            existing.Reason = request.Reason;
            existing.Detail = Trim(request.Detail);
            existing.BlockedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return true;
        }

        db.ReleaseBlocklist.Add(new ReleaseBlocklistEntity
        {
            InfoHash = hash,
            NormalizedReleaseName = normalizedName,
            ReleaseName = request.ReleaseName is { Length: > 512 } n ? n[..512] : request.ReleaseName,
            Scope = BlocklistScope.Request,
            MediaRequestId = job.MediaRequestId,
            MediaId = job.MediaId,
            MediaType = job.MediaType,
            Season = request.Season,
            Episode = request.Episode,
            IndexerId = request.IndexerId,
            Reason = request.Reason,
            Detail = Trim(request.Detail)
        });
        await db.SaveChangesAsync();

        logger.LogInformation("Blocklisted \"{Release}\" for request #{RequestId}: {Reason}",
            request.ReleaseName, job.MediaRequestId, request.Reason);
        return true;
    }

    public async Task<List<BlocklistEntryDto>> ListAsync(int? mediaRequestId = null, int take = 200)
    {
        var q = db.ReleaseBlocklist.AsNoTracking().AsQueryable();
        if (mediaRequestId is int rid) q = q.Where(b => b.MediaRequestId == rid);

        var rows = await q.OrderByDescending(b => b.BlockedAt).Take(Math.Clamp(take, 1, 1000)).ToListAsync();
        var titles = await db.MediaRequests.AsNoTracking()
            .Where(r => rows.Select(x => x.MediaRequestId).Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Title);

        return rows.Select(b => new BlocklistEntryDto
        {
            Id = b.Id, InfoHash = b.InfoHash, ReleaseName = b.ReleaseName, Scope = b.Scope,
            Reason = b.Reason, Detail = b.Detail, MediaRequestId = b.MediaRequestId,
            RequestTitle = b.MediaRequestId is int id && titles.TryGetValue(id, out var t) ? t : null,
            MediaId = b.MediaId, MediaType = b.MediaType, BlockedAt = b.BlockedAt, ExpiresAt = b.ExpiresAt
        }).ToList();
    }

    public async Task<bool> UnblockAsync(int id)
    {
        var row = await db.ReleaseBlocklist.FirstOrDefaultAsync(b => b.Id == id);
        if (row is null) return false;
        db.ReleaseBlocklist.Remove(row);
        await db.SaveChangesAsync();
        logger.LogInformation("Unblocked \"{Release}\"", row.ReleaseName);
        return true;
    }

    public async Task<int> ClearForRequestAsync(int mediaRequestId) =>
        await db.ReleaseBlocklist.Where(b => b.MediaRequestId == mediaRequestId).ExecuteDeleteAsync();

    public async Task<List<string>> HashesForRequestAsync(int mediaRequestId)
    {
        var job = await db.FulfillmentJobs.AsNoTracking()
            .Where(j => j.Id == mediaRequestId || j.MediaRequestId == mediaRequestId)
            .Select(j => new { j.MediaId, j.MediaType, j.MediaRequestId }).FirstOrDefaultAsync();
        if (job is null) return new();

        var now = DateTime.UtcNow;
        return await db.ReleaseBlocklist
            .Where(b => b.InfoHash != null && (b.ExpiresAt == null || b.ExpiresAt > now))
            // Request-scoped entries apply to this request; wider scopes apply to the title or everywhere.
            .Where(b => b.MediaRequestId == job.MediaRequestId
                        || (b.Scope == BlocklistScope.Media && b.MediaId == job.MediaId && b.MediaType == job.MediaType)
                        || b.Scope == BlocklistScope.Global)
            .Select(b => b.InfoHash!)
            .Distinct()
            .ToListAsync();
    }

    public async Task<int> PruneExpiredAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await db.ReleaseBlocklist.Where(b => b.ExpiresAt != null && b.ExpiresAt <= now).ExecuteDeleteAsync(ct);
    }

    private static string? Trim(string? s) => s is { Length: > 1000 } ? s[..1000] : s;
}
