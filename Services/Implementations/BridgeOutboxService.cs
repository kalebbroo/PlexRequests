using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// Persistence and delivery contract for the external Discord bridge. Rows are immutable event snapshots,
/// so a bot outage, an app restart, or later metadata changes cannot erase the facts it still needs to see.
/// </summary>
public sealed class BridgeOutboxService(
    AppDbContext db,
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<BridgeOutboxService> logger) : IBridgeOutboxService
{
    private readonly AppDbContext _db = db;
    private readonly IServiceProvider _services = services;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<BridgeOutboxService> _logger = logger;

    public int RetentionDays => Math.Clamp(_configuration.GetValue("Bridge:EventRetentionDays", 30), 1, 365);

    public async Task EnqueueAsync(MediaRequestDto request, BridgeEventType type, string? detail = null,
        CancellationToken cancellationToken = default)
    {
        if (!_configuration.GetValue<bool>("Bridge:Enabled")) return;

        var canonical = await _db.MediaRequests.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken);
        var row = canonical is null
            ? CreateRow(request, type, detail)
            : CreateRow(canonical, type, detail);

        if (await _db.BridgeOutbox.AnyAsync(x => x.DeduplicationKey == row.DeduplicationKey,
                cancellationToken))
            return;

        _db.BridgeOutbox.Add(row);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A notification and a repair poll may race. The unique key turns that race into success,
            // while still surfacing unrelated persistence failures.
            _db.Entry(row).State = EntityState.Detached;
            if (!await _db.BridgeOutbox.AsNoTracking()
                    .AnyAsync(x => x.DeduplicationKey == row.DeduplicationKey, cancellationToken))
                throw;
        }
    }

    public async Task<BridgeEventBatchDto> ReadBatchAsync(long cursor, int take,
        CancellationToken cancellationToken = default)
    {
        cursor = Math.Max(0, cursor);
        take = Math.Clamp(take, 1, 100);

        await PruneExpiredAsync(cancellationToken);
        await RepairMissingCurrentStateEventsAsync(cancellationToken);

        var oldestCursor = await _db.BridgeOutbox.MinAsync(x => (long?)x.Id, cancellationToken) ?? 0;

        var rows = await _db.BridgeOutbox.AsNoTracking()
            .Where(x => x.Id > cursor)
            .OrderBy(x => x.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        var requesterIds = rows.Where(x => x.RequesterUserId.HasValue)
            .Select(x => x.RequesterUserId!.Value).Distinct().ToList();
        var discordRecipients = requesterIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.UserProfiles.AsNoTracking()
                .Where(x => requesterIds.Contains(x.UserId) && x.DiscordDmOptIn && x.DiscordUserId != null)
                .ToDictionaryAsync(x => x.UserId, x => x.DiscordUserId!, cancellationToken);

        var events = new List<BridgeEventDto>(rows.Count);
        foreach (var row in rows)
        {
            var media = ResolveMedia(row);
            var dto = new BridgeEventDto
            {
                Cursor = row.Id,
                EventId = row.DeduplicationKey,
                Type = row.EventType,
                RequestId = row.MediaRequestId,
                MediaId = row.MediaId,
                MediaType = row.MediaType,
                Media = media,
                RequestScope = row.RequestScopeKind,
                Title = row.Title,
                PosterUrl = row.PosterUrl,
                Status = row.Status,
                Detail = row.Detail,
                RequesterName = row.RequesterName,
                RequesterDiscordId = row.RequesterUserId is int userId
                    && discordRecipients.TryGetValue(userId, out var discordId) ? discordId : null,
                CreatedAt = row.CreatedAt
            };

            if (media.TryGetTmdbId(out var tmdbId)) dto.TmdbId = tmdbId;
            await EnrichAsync(dto, media);
            events.Add(dto);
        }

        return new BridgeEventBatchDto
        {
            RequestedCursor = cursor,
            OldestAvailableCursor = oldestCursor,
            NextCursor = events.Count == 0 ? cursor : events[^1].Cursor,
            HasMore = hasMore,
            CursorExpired = cursor > 0 && oldestCursor > 0 && cursor < oldestCursor - 1,
            Events = events
        };
    }

    public async Task<BridgeEventAckResult> AcknowledgeAsync(long cursor,
        CancellationToken cancellationToken = default)
    {
        var highest = await _db.BridgeOutbox.MaxAsync(x => (long?)x.Id, cancellationToken) ?? 0;
        var bounded = Math.Clamp(cursor, 0, highest);
        var now = DateTime.UtcNow;
        var acknowledged = bounded == 0 ? 0 : await _db.BridgeOutbox
            .Where(x => x.Id <= bounded && x.AcknowledgedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.AcknowledgedAt, now), cancellationToken);
        var pruned = await PruneExpiredAsync(cancellationToken);
        return new BridgeEventAckResult(bounded, acknowledged, pruned);
    }

    private async Task EnrichAsync(BridgeEventDto dto, MediaRef media)
    {
        try
        {
            // Metadata is resolved only by a polling reader. Normal request notifications—especially when
            // the optional bridge is disabled—do not needlessly construct the metadata provider graph.
            var details = await _services.GetRequiredService<IMediaMetadataProvider>().GetDetailsAsync(media);
            if (details is null) return;
            dto.Overview = details.Overview;
            dto.BackdropUrl = details.BackdropUrl;
            dto.Rating = details.Rating;
            dto.Year = details.Year;
            dto.TmdbId = details.TmdbId ?? dto.TmdbId;
            dto.Genres = details.Genres;
            if (string.IsNullOrWhiteSpace(dto.PosterUrl)) dto.PosterUrl = details.PosterUrl;
        }
        catch (Exception ex)
        {
            // The durable event is still deliverable when an optional metadata service is down.
            _logger.LogDebug(ex, "Could not enrich bridge event {EventId}", dto.EventId);
        }
    }

    private async Task RepairMissingCurrentStateEventsAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        var requests = await _db.MediaRequests.AsNoTracking()
            .Where(x => x.RequestedAt >= cutoff || x.AvailableAt >= cutoff)
            .ToListAsync(cancellationToken);
        if (requests.Count == 0) return;

        var repairs = requests.Select(x => (Request: x, Event: CurrentEvent(x))).ToList();
        var existing = (await _db.BridgeOutbox.AsNoTracking()
                .Where(x => x.CreatedAt >= cutoff)
                .Select(x => x.DeduplicationKey)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var missing = repairs
            .Select(x => CreateRow(x.Request, x.Event.Type, x.Event.Detail))
            .Where(x => !existing.Contains(x.DeduplicationKey))
            .DistinctBy(x => x.DeduplicationKey)
            .ToList();
        if (missing.Count == 0) return;

        _db.BridgeOutbox.AddRange(missing);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Repaired {Count} missing bridge lifecycle event(s)", missing.Count);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent notification may have repaired the same state. Clear this scoped tracker and
            // let the next poll verify anything else; unique keys guarantee no duplicate delivery.
            _db.ChangeTracker.Clear();
            _logger.LogDebug(ex, "Bridge repair raced another event writer; a later poll will reconcile");
        }
    }

    private async Task<int> PruneExpiredAsync(CancellationToken cancellationToken)
    {
        var eventCutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        // Acknowledged rows remain as deduplication tombstones for the same bounded retention period.
        // Removing them earlier would let the repair pass recreate and redeliver an already handled event.
        return await _db.BridgeOutbox
            .Where(x => x.CreatedAt < eventCutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static (BridgeEventType Type, string? Detail) CurrentEvent(MediaRequestEntity request) =>
        request.Status switch
        {
            RequestStatus.Pending => (BridgeEventType.Created, null),
            RequestStatus.Approved or RequestStatus.Processing => (BridgeEventType.Approved, null),
            RequestStatus.Searching => (BridgeEventType.Searching, request.DenialReason),
            RequestStatus.PartiallyAvailable => (BridgeEventType.PartiallyAvailable, request.DenialReason),
            RequestStatus.Available => (BridgeEventType.Available, null),
            RequestStatus.Failed => (BridgeEventType.Failed, request.DenialReason),
            RequestStatus.Rejected => (BridgeEventType.Denied, request.DenialReason),
            RequestStatus.Cancelled => (BridgeEventType.Cancelled, null),
            _ => (BridgeEventType.Created, null)
        };

    private static BridgeOutboxEntity CreateRow(MediaRequestEntity request, BridgeEventType type,
        string? detail)
    {
        var media = ResolveMedia(request);
        return CreateRow(request.Id, request.MediaId, media, request.RequestScopeKind, request.Title,
            request.PosterUrl, request.Status, request.RequestedByUserId, request.RequestedBy, type, detail);
    }

    private static BridgeOutboxEntity CreateRow(MediaRequestDto request, BridgeEventType type, string? detail)
    {
        var media = request.MediaRef is { IsValid: true }
            ? request.MediaRef
            : !string.IsNullOrWhiteSpace(request.ExternalId)
                ? MediaRef.FromExternal(request.ExternalSource ?? "external", request.ExternalId,
                    request.MediaType, request.RequestScopeKind.ToMediaKind(request.MediaType))
                : LegacyMedia(request.Id, request.MediaId, request.MediaType, request.RequestScopeKind);
        return CreateRow(request.Id, request.MediaId, media, request.RequestScopeKind, request.Title,
            request.PosterUrl, request.Status, request.RequestedByUserId > 0 ? request.RequestedByUserId : null,
            request.RequestedByUsername, type, detail);
    }

    private static BridgeOutboxEntity CreateRow(int requestId, int mediaId, MediaRef media,
        RequestScopeKind scope, string title, string? posterUrl, RequestStatus status, int? requesterUserId,
        string? requesterName, BridgeEventType type, string? detail) => new()
        {
            DeduplicationKey = BuildDeduplicationKey(requestId, type, detail),
            EventType = type,
            MediaRequestId = requestId,
            MediaId = mediaId,
            MediaType = media.MediaType,
            MediaKind = media.Kind,
            RequestScopeKind = scope,
            ExternalSource = Limit(media.Provider, 32),
            ExternalId = Limit(media.Id, 128),
            Title = Limit(title, 512) ?? string.Empty,
            PosterUrl = Limit(posterUrl, 512),
            Status = status,
            RequesterUserId = requesterUserId,
            RequesterName = Limit(requesterName, 128),
            Detail = Limit(detail, 1000),
            CreatedAt = DateTime.UtcNow
        };

    private static MediaRef ResolveMedia(MediaRequestEntity request) =>
        !string.IsNullOrWhiteSpace(request.ExternalId)
            ? MediaRef.FromExternal(request.ExternalSource ?? "external", request.ExternalId,
                request.MediaType, request.RequestScopeKind.ToMediaKind(request.MediaType))
            : LegacyMedia(request.Id, request.MediaId, request.MediaType, request.RequestScopeKind);

    private static MediaRef ResolveMedia(BridgeOutboxEntity row) =>
        !string.IsNullOrWhiteSpace(row.ExternalId)
            ? MediaRef.FromExternal(row.ExternalSource ?? "external", row.ExternalId, row.MediaType, row.MediaKind)
            : LegacyMedia(row.MediaRequestId, row.MediaId, row.MediaType, row.RequestScopeKind);

    private static MediaRef LegacyMedia(int requestId, int mediaId, MediaType mediaType,
        RequestScopeKind scope) => mediaId > 0 && mediaType != MediaType.Music
        ? MediaRef.FromTmdb(mediaId, mediaType)
        : MediaRef.FromExternal("legacy-request", requestId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            mediaType, scope.ToMediaKind(mediaType));

    private static string BuildDeduplicationKey(int requestId, BridgeEventType type, string? detail)
    {
        var prefix = $"request:{requestId}:event:{(int)type}";
        if (type != BridgeEventType.Upgraded || string.IsNullOrWhiteSpace(detail))
            return prefix;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(detail.Trim()));
        return $"{prefix}:{Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    private static string? Limit(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
