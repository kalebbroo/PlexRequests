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

    /// <summary>Legacy torrent hashes plus protocol-qualified source ids to exclude for this request.</summary>
    Task<List<string>> HashesForRequestAsync(int mediaRequestId);

    /// <summary>Delete lapsed entries. Called by the cleanup job.</summary>
    Task<int> PruneExpiredAsync(CancellationToken ct = default);
}

/// <summary>Versioned automatic decisions can be reconsidered after mapping logic changes. Increment the
/// relevant version whenever a release previously rejected for that reason may now be mapped safely.</summary>
public static class ReleaseBlocklistPolicy
{
    public const int CurrentEpisodeMappingVersion = 2;

    public static bool IsDurableContentDecision(BlocklistReason reason) => reason is
        BlocklistReason.WrongContent or BlocklistReason.ManualBlock or BlocklistReason.MediaPolicyMismatch;

    public static IQueryable<ReleaseBlocklistEntity> EffectiveAt(
        this IQueryable<ReleaseBlocklistEntity> query, DateTime now) => query.Where(entry =>
        (entry.ExpiresAt == null || entry.ExpiresAt > now)
        && (entry.Reason != BlocklistReason.EpisodeMappingAmbiguous
            || entry.DecisionVersion >= CurrentEpisodeMappingVersion));

    public static int? DecisionVersionFor(BlocklistReason reason) =>
        reason == BlocklistReason.EpisodeMappingAmbiguous ? CurrentEpisodeMappingVersion : null;
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
        var hash = request.Protocol == AcquisitionProtocol.Torrent ? MagnetUtil.Normalize(request.InfoHash) : null;
        var sourceId = NormalizeSourceId(request.SourceId ?? hash);
        var normalizedName = string.IsNullOrWhiteSpace(request.ReleaseName)
            ? null
            : ReleaseBlocklistEntity.Normalize(request.ReleaseName);

        // The import audit is written before backend cleanup. If another observer sees the cleaned-up
        // transfer as missing/stalled, that is a successful lifecycle transition—not a broken release.
        // Deliberate content/policy blocks remain allowed so an admin can replace a previously imported but
        // genuinely wrong file.
        var disappearanceReason = request.Reason is BlocklistReason.DownloadFailed
            or BlocklistReason.Stalled or BlocklistReason.TorrentError or BlocklistReason.PathUnresolvable;
        if (disappearanceReason && await db.ImportedFiles.AsNoTracking().AnyAsync(f =>
                f.FulfillmentJobId == job.Id && f.Protocol == request.Protocol
                && ((sourceId != null && f.TransferId == sourceId)
                    || (hash != null && f.InfoHash == hash))))
        {
            logger.LogInformation(
                "Ignored {Reason} block for imported transfer {SourceId} on job {JobId}",
                request.Reason, sourceId ?? hash, job.Id);
            return false;
        }

        if (sourceId is null && normalizedName is null)
        {
            logger.LogDebug("Blocklist entry skipped for job {JobId}: neither a source id nor a release name", fulfillmentJobId);
            return false;
        }

        // The unique (InfoHash, MediaRequestId) index means re-failing the same torrent updates rather than
        // piling up rows.
        var existing = await db.ReleaseBlocklist.FirstOrDefaultAsync(b =>
            b.MediaRequestId == job.MediaRequestId &&
            ((sourceId != null && b.Protocol == request.Protocol && b.SourceId == sourceId)
             || (hash != null && b.InfoHash == hash)
             || (sourceId == null && b.NormalizedReleaseName == normalizedName)));

        if (existing is not null)
        {
            // A later automatic parser failure must never weaken a deliberate or immutable content decision.
            // The inverse remains allowed: an automatic mapping rejection can be upgraded to a durable block.
            if (request.Reason == BlocklistReason.EpisodeMappingAmbiguous
                && ReleaseBlocklistPolicy.IsDurableContentDecision(existing.Reason))
            {
                logger.LogInformation(
                    "Kept durable {ExistingReason} block for {Release}; ignored automatic {NewReason} update",
                    existing.Reason, request.ReleaseName, request.Reason);
                return true;
            }

            existing.Reason = request.Reason;
            existing.DecisionVersion = ReleaseBlocklistPolicy.DecisionVersionFor(request.Reason);
            existing.Detail = Trim(request.Detail);
            existing.BlockedAt = DateTime.UtcNow;
            existing.ExpiresAt = request.ExpiresAt;
            await db.SaveChangesAsync();
            return true;
        }

        db.ReleaseBlocklist.Add(new ReleaseBlocklistEntity
        {
            InfoHash = hash,
            Protocol = request.Protocol,
            SourceId = sourceId,
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
            DecisionVersion = ReleaseBlocklistPolicy.DecisionVersionFor(request.Reason),
            Detail = Trim(request.Detail),
            ExpiresAt = request.ExpiresAt
        });
        await db.SaveChangesAsync();

        logger.LogInformation("Blocklisted \"{Release}\" for request #{RequestId}: {Reason}",
            request.ReleaseName, job.MediaRequestId, request.Reason);
        return true;
    }

    public async Task<List<BlocklistEntryDto>> ListAsync(int? mediaRequestId = null, int take = 200)
    {
        var q = db.ReleaseBlocklist.AsNoTracking().EffectiveAt(DateTime.UtcNow);
        if (mediaRequestId is int rid) q = q.Where(b => b.MediaRequestId == rid);

        var rows = await q.OrderByDescending(b => b.BlockedAt).Take(Math.Clamp(take, 1, 1000)).ToListAsync();
        var titles = await db.MediaRequests.AsNoTracking()
            .Where(r => rows.Select(x => x.MediaRequestId).Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Title);

        return rows.Select(b => new BlocklistEntryDto
        {
            Id = b.Id, InfoHash = b.InfoHash, Protocol = b.Protocol, SourceId = b.SourceId,
            ReleaseName = b.ReleaseName, Scope = b.Scope,
            Reason = b.Reason, DecisionVersion = b.DecisionVersion, Detail = b.Detail,
            MediaRequestId = b.MediaRequestId,
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
        var resources = await db.ReleaseBlocklist.EffectiveAt(now)
            .Where(b => b.SourceId != null || b.InfoHash != null)
            // Request-scoped entries apply to this request; wider scopes apply to the title or everywhere.
            .Where(b => b.MediaRequestId == job.MediaRequestId
                        || (b.Scope == BlocklistScope.Media && b.MediaId == job.MediaId && b.MediaType == job.MediaType)
                        || b.Scope == BlocklistScope.Global)
            .Select(b => new { b.Protocol, b.SourceId, b.InfoHash }).ToListAsync();
        return resources.SelectMany(x => new[]
            {
                x.InfoHash,
                x.SourceId is null ? null : AcquisitionResource.BlocklistKey(x.Protocol, x.SourceId)
            })
            .Where(x => x is not null).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<int> PruneExpiredAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await db.ReleaseBlocklist.Where(b =>
                (b.ExpiresAt != null && b.ExpiresAt <= now)
                || (b.Reason == BlocklistReason.EpisodeMappingAmbiguous
                    && (b.DecisionVersion == null
                        || b.DecisionVersion < ReleaseBlocklistPolicy.CurrentEpisodeMappingVersion)))
            .ExecuteDeleteAsync(ct);
    }

    private static string? Trim(string? s) => s is { Length: > 1000 } ? s[..1000] : s;
    private static string? NormalizeSourceId(string? sourceId)
    {
        var value = sourceId?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(value) ? null : value.Length > 256 ? value[..256] : value;
    }
}
