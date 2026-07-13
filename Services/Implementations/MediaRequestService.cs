using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;

namespace PlexRequestsHosted.Services.Implementations;

public class MediaRequestService(
    AppDbContext db,
    AuthenticationStateProvider authStateProvider,
    IMediaMetadataProvider metadataProvider,
    INotificationService notificationService,
    IFulfillmentQueue fulfillmentQueue,
    IConfiguration configuration,
    IDownloadPreferencesService downloadPreferences) : IMediaRequestService
{
    private readonly AppDbContext _db = db;
    private readonly AuthenticationStateProvider _auth = authStateProvider;
    private readonly IMediaMetadataProvider _metadata = metadataProvider;
    private readonly INotificationService _notify = notificationService;
    private readonly IFulfillmentQueue _fulfillment = fulfillmentQueue;
    private readonly IConfiguration _config = configuration;
    private readonly IDownloadPreferencesService _downloadPreferences = downloadPreferences;

    private async Task<(string username, bool isAdmin)> GetUserAsync()
    {
        var state = await _auth.GetAuthenticationStateAsync();
        var user = state.User;
        var name = user.Identity?.Name ?? "";
        var isAdmin = user.IsInRole("Admin");
        return (name, isAdmin);
    }

    public async Task<bool> AddToWatchlistAsync(int mediaId, MediaType mediaType)
    {
        var (username, _) = await GetUserAsync();
        if (string.IsNullOrWhiteSpace(username)) return false;
        var userId = await _db.Users.Where(u => u.Username == username).Select(u => (int?)u.Id).FirstOrDefaultAsync();
        // Avoid duplicates for the same (user, media, type)
        var already = await _db.Watchlist.AnyAsync(w => w.MediaId == mediaId && w.MediaType == mediaType && w.Username == username);
        if (already) return true;
        _db.Watchlist.Add(new WatchlistItemEntity
        {
            MediaId = mediaId,
            MediaType = mediaType,
            UserId = userId,
            Username = username
        });
        return await _db.SaveChangesAsync() > 0;
    }

    public async Task<bool> CancelRequestAsync(int requestId)
    {
        var (username, isAdmin) = await GetUserAsync();
        var req = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (req is null) return false;
        if (!isAdmin && !string.Equals(req.RequestedBy, username, StringComparison.OrdinalIgnoreCase)) return false;
        req.Status = RequestStatus.Cancelled;
        return await _db.SaveChangesAsync() > 0;
    } // TODO: Add authorization check for request ownership

    public async Task<UserStatsDto> GetMyStatsAsync()
    {
        var (username, isAdmin) = await GetUserAsync();
        var q = _db.MediaRequests.AsQueryable();
        if (!isAdmin)
            q = q.Where(r => r.RequestedBy == username);
        var stats = new UserStatsDto
        {
            TotalRequests = await q.CountAsync(),
            ApprovedRequests = await q.CountAsync(r => r.Status == RequestStatus.Approved),
            PendingRequests = await q.CountAsync(r => r.Status == RequestStatus.Pending),
            AvailableRequests = await q.CountAsync(r => r.Status == RequestStatus.Available),
            LastRequestDate = await q.OrderByDescending(r => r.RequestedAt).Select(r => (DateTime?)r.RequestedAt).FirstOrDefaultAsync()
        };
        return stats;
    } // TODO: Filter by current authenticated user

    public async Task<List<MediaCardDto>> GetWatchlistAsync()
    {
        var (username, _) = await GetUserAsync();
        var items = await _db.Watchlist
            .Where(w => w.Username == username)
            .OrderByDescending(w => w.AddedAt)
            .Select(w => new { w.MediaId, w.MediaType })
            .ToListAsync();

        var cards = new List<MediaCardDto>(items.Count);
        foreach (var it in items)
        {
            MediaCardDto? card = null;
            try
            {
                var d = await _metadata.GetDetailsAsync(it.MediaId, it.MediaType);
                if (d is not null)
                {
                    card = new MediaCardDto
                    {
                        Id = d.Id,
                        Title = d.Title,
                        Overview = d.Overview,
                        PosterUrl = d.PosterUrl,
                        BackdropUrl = d.BackdropUrl,
                        Year = d.Year,
                        Rating = d.Rating,
                        MediaType = it.MediaType,
                        Genres = d.Genres,
                        TmdbId = d.TmdbId,
                        RequestStatus = RequestStatus.None
                    };
                }
            }
            catch { /* metadata provider unavailable; fall back to a minimal card */ }

            card ??= new MediaCardDto { Id = it.MediaId, Title = $"Item #{it.MediaId}", MediaType = it.MediaType, RequestStatus = RequestStatus.None };
            cards.Add(card);
        }
        return cards;
    }

    public async Task<MediaRequestDto?> GetRequestByIdAsync(int id)
    {
        var r = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == id);
        return r == null ? null : new MediaRequestDto
        {
            Id = r.Id,
            MediaId = r.MediaId,
            MediaType = r.MediaType,
            Title = r.Title,
            PosterUrl = r.PosterUrl,
            Status = r.Status,
            RequestedAt = r.RequestedAt,
            ApprovedAt = r.ApprovedAt,
            AvailableAt = r.AvailableAt,
            DenialReason = r.DenialReason,
            RequestNote = r.RequestNote,
            RequestAllSeasons = r.RequestAllSeasons,
            RequestedEpisodesCsv = r.RequestedEpisodesCsv,
            Monitored = r.Monitored,
            ExternalId = r.ExternalId,
            ExternalSource = r.ExternalSource,
            RequestedSeasons = string.IsNullOrWhiteSpace(r.RequestedSeasonsCsv) ? new List<int>() : r.RequestedSeasonsCsv.Split(',').Select(s => int.TryParse(s, out var n) ? n : (int?)null).Where(n => n.HasValue).Select(n => n!.Value).ToList(),
            RequestedByUserId = r.RequestedByUserId ?? 0,
            RequestedByUsername = r.RequestedBy ?? string.Empty
        };
    } // TODO: Add authorization check

    public async Task<PagedResult<MediaRequestDto>> GetRequestsAsync(MediaFilterDto filter)
    {
        var (username, isAdmin) = await GetUserAsync();
        var query = _db.MediaRequests.AsQueryable();
        if (!isAdmin)
            query = query.Where(r => r.RequestedBy == username);
        query = query.OrderByDescending(r => r.RequestedAt);
        var total = await query.CountAsync();
        var entities = await query
            .Skip((filter.PageNumber - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync();

        var items = new List<MediaRequestDto>(entities.Count);
        var changed = false;
        foreach (var r in entities)
        {
            // Backfill title/poster if missing for better UI
            if ((string.IsNullOrWhiteSpace(r.Title) || r.Title.StartsWith("Item #")) || string.IsNullOrWhiteSpace(r.PosterUrl))
            {
                try
                {
                    var d = await _metadata.GetDetailsAsync(r.MediaId, r.MediaType);
                    if (d is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(d.Title) && (string.IsNullOrWhiteSpace(r.Title) || r.Title.StartsWith("Item #")))
                        { r.Title = d.Title; changed = true; }
                        if (string.IsNullOrWhiteSpace(r.PosterUrl) && !string.IsNullOrWhiteSpace(d.PosterUrl))
                        { r.PosterUrl = d.PosterUrl; changed = true; }
                    }
                }
                catch { /* ignore */ }
            }

            items.Add(new MediaRequestDto
            {
                Id = r.Id,
                MediaId = r.MediaId,
                MediaType = r.MediaType,
                Title = r.Title,
                PosterUrl = r.PosterUrl,
                Status = r.Status,
                RequestedAt = r.RequestedAt,
                ApprovedAt = r.ApprovedAt,
                AvailableAt = r.AvailableAt,
                DenialReason = r.DenialReason,
                RequestNote = r.RequestNote,
                RequestAllSeasons = r.RequestAllSeasons,
            RequestedEpisodesCsv = r.RequestedEpisodesCsv,
            Monitored = r.Monitored,
            ExternalId = r.ExternalId,
            ExternalSource = r.ExternalSource,
                RequestedSeasons = string.IsNullOrWhiteSpace(r.RequestedSeasonsCsv) ? new List<int>() : r.RequestedSeasonsCsv.Split(',').Select(s => { int n; return int.TryParse(s, out n) ? (int?)n : null; }).Where(n => n.HasValue).Select(n => n!.Value).ToList(),
                RequestedByUserId = r.RequestedByUserId ?? 0,
                RequestedByUsername = r.RequestedBy ?? string.Empty
            });
        }
        if (changed)
        {
            try { await _db.SaveChangesAsync(); } catch { /* non-fatal */ }
        }
        return new PagedResult<MediaRequestDto> { Items = items, TotalCount = total, PageNumber = filter.PageNumber, PageSize = filter.PageSize };
    } // TODO: Add user filtering and admin permissions

    public async Task<bool> IsInWatchlistAsync(int mediaId, MediaType mediaType)
    {
        var (username, _) = await GetUserAsync();
        return await _db.Watchlist.AnyAsync(w => w.MediaId == mediaId && w.MediaType == mediaType && w.Username == username);
    }

    public async Task<MediaRequestResult> RequestMediaAsync(int mediaId, MediaType mediaType)
    {
        var (username, isAdmin) = await GetUserAsync();
        if (string.IsNullOrWhiteSpace(username)) return new MediaRequestResult { Success = false, ErrorMessage = "Not authenticated" };
        var userId = await _db.Users.Where(u => u.Username == username).Select(u => (int?)u.Id).FirstOrDefaultAsync();
        if (userId is null) return new MediaRequestResult { Success = false, ErrorMessage = "User not found" };
        return await CreateRequestCoreAsync(userId.Value, username, isAdmin, mediaId, mediaType);
    }

    /// <summary>Request specific seasons of a TV show (empty list ⇒ the whole series).</summary>
    public async Task<MediaRequestResult> RequestSeasonsAsync(int mediaId, MediaType mediaType, List<int> seasons)
    {
        var (username, isAdmin) = await GetUserAsync();
        if (string.IsNullOrWhiteSpace(username)) return new MediaRequestResult { Success = false, ErrorMessage = "Not authenticated" };
        var userId = await _db.Users.Where(u => u.Username == username).Select(u => (int?)u.Id).FirstOrDefaultAsync();
        if (userId is null) return new MediaRequestResult { Success = false, ErrorMessage = "User not found" };
        var allSeasons = seasons is not { Count: > 0 };
        return await CreateRequestCoreAsync(userId.Value, username, isAdmin, mediaId, mediaType, allSeasons, seasons);
    }

    /// <summary>Request specific episodes of a TV show, e.g. [(1,1),(1,2),(2,5)].</summary>
    public async Task<MediaRequestResult> RequestEpisodesAsync(int mediaId, MediaType mediaType, List<(int season, int episode)> episodes)
    {
        var (username, isAdmin) = await GetUserAsync();
        if (string.IsNullOrWhiteSpace(username)) return new MediaRequestResult { Success = false, ErrorMessage = "Not authenticated" };
        if (episodes is not { Count: > 0 }) return new MediaRequestResult { Success = false, ErrorMessage = "No episodes selected" };
        var userId = await _db.Users.Where(u => u.Username == username).Select(u => (int?)u.Id).FirstOrDefaultAsync();
        if (userId is null) return new MediaRequestResult { Success = false, ErrorMessage = "User not found" };
        return await CreateRequestCoreAsync(userId.Value, username, isAdmin, mediaId, mediaType, allSeasons: false, seasons: null, episodes: episodes);
    }

    /// <summary>
    /// Create an auto-approved child request for a single newly-aired episode of a monitored series,
    /// and enqueue it. Used by SeriesMonitorService — flows through the normal one-request/one-job
    /// pipeline, so the fulfilled callback marks THIS child available (the monitored anchor is untouched).
    /// </summary>
    public async Task<MediaRequestResult> CreateMonitoredEpisodeAsync(int anchorRequestId, int season, int episode)
    {
        var anchor = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == anchorRequestId);
        if (anchor is null) return new MediaRequestResult { Success = false, ErrorMessage = "Anchor request not found" };

        var child = new MediaRequestEntity
        {
            MediaId = anchor.MediaId,
            MediaType = anchor.MediaType,
            Title = anchor.Title,
            PosterUrl = anchor.PosterUrl,
            Status = RequestStatus.Approved,
            RequestedAt = DateTime.UtcNow,
            ApprovedAt = DateTime.UtcNow,
            RequestedBy = anchor.RequestedBy,
            RequestedByUserId = anchor.RequestedByUserId,
            RequestAllSeasons = false,
            RequestedEpisodesCsv = $"S{season}E{episode}",
            Monitored = false
        };
        _db.MediaRequests.Add(child);
        await _db.SaveChangesAsync();

        if (_config.GetValue<bool>("Fulfillment:Enabled"))
            await _fulfillment.EnqueueAsync(ToDto(child));

        return new MediaRequestResult { Success = true, RequestId = child.Id, NewStatus = child.Status };
    }

    /// <summary>
    /// SCAFFOLD: request an album/artist by provider id. Music has no TMDb int id, so it flows on the
    /// string <see cref="MediaRequestEntity.ExternalId"/> instead of MediaId.
    /// TODO(music): (1) de-dup against Plex via IPlexMusicService.IsAlbumOnPlexAsync before creating;
    /// (2) enqueue needs a music path in FulfillmentQueue that targets ExternalId, and a music indexer
    /// in the downloader; (3) availability reconciliation should match music by ExternalId/name.
    /// </summary>
    public async Task<MediaRequestResult> RequestMusicAsync(string externalId, string source, string title, string? posterUrl = null)
    {
        var (username, isAdmin) = await GetUserAsync();
        if (string.IsNullOrWhiteSpace(username)) return new MediaRequestResult { Success = false, ErrorMessage = "Not authenticated" };
        var userId = await _db.Users.Where(u => u.Username == username).Select(u => (int?)u.Id).FirstOrDefaultAsync();
        if (userId is null) return new MediaRequestResult { Success = false, ErrorMessage = "User not found" };

        var autoApprove = isAdmin || await _db.UserProfiles.Where(p => p.UserId == userId).Select(p => p.AutoApprove).FirstOrDefaultAsync();
        var entity = new MediaRequestEntity
        {
            MediaId = 0,                       // no TMDb id for music
            MediaType = MediaType.Music,
            ExternalId = externalId,
            ExternalSource = string.IsNullOrWhiteSpace(source) ? "musicbrainz" : source,
            Title = title,
            PosterUrl = posterUrl,
            Status = autoApprove ? RequestStatus.Approved : RequestStatus.Pending,
            RequestedAt = DateTime.UtcNow,
            ApprovedAt = autoApprove ? DateTime.UtcNow : null,
            RequestedBy = username,
            RequestedByUserId = userId
        };
        _db.MediaRequests.Add(entity);
        var saved = await _db.SaveChangesAsync() > 0;
        if (saved)
        {
            var dto = ToDto(entity);
            if (autoApprove) await _notify.RequestApprovedAsync(dto); else await _notify.RequestCreatedAsync(dto);
            // TODO(music): if (autoApprove && Fulfillment:Enabled) enqueue a music job (needs downloader support).
        }
        return new MediaRequestResult { Success = saved, RequestId = entity.Id, NewStatus = entity.Status };
    }

    /// <summary>Request an entire series. Whether it's then monitored for new episodes as they air is an
    /// admin-configured default (<see cref="DownloadPreferencesDto.AutoMonitorEntireSeriesRequests"/>), not
    /// a per-request user choice.</summary>
    public async Task<MediaRequestResult> RequestSeriesAsync(int mediaId, MediaType mediaType)
    {
        var (username, isAdmin) = await GetUserAsync();
        if (string.IsNullOrWhiteSpace(username)) return new MediaRequestResult { Success = false, ErrorMessage = "Not authenticated" };
        var userId = await _db.Users.Where(u => u.Username == username).Select(u => (int?)u.Id).FirstOrDefaultAsync();
        if (userId is null) return new MediaRequestResult { Success = false, ErrorMessage = "User not found" };
        var monitor = (await _downloadPreferences.GetAsync()).AutoMonitorEntireSeriesRequests;
        return await CreateRequestCoreAsync(userId.Value, username, isAdmin, mediaId, mediaType, allSeasons: true, seasons: null, episodes: null, monitored: monitor);
    }

    /// <summary>Create a request on behalf of an explicit user (used by the Discord bridge, which has no cookie session).</summary>
    public async Task<MediaRequestResult> RequestMediaForUserAsync(int userId, int mediaId, MediaType mediaType)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return new MediaRequestResult { Success = false, ErrorMessage = "User not found" };
        var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
        var isAdmin = (profile?.Roles ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains("Admin", StringComparer.OrdinalIgnoreCase);
        return await CreateRequestCoreAsync(userId, user.Username, isAdmin, mediaId, mediaType);
    }

    private async Task<MediaRequestResult> CreateRequestCoreAsync(int userId, string username, bool isAdmin, int mediaId, MediaType mediaType, bool allSeasons = true, List<int>? seasons = null, List<(int season, int episode)>? episodes = null, bool monitored = false)
    {
        // Per-user duplicate check: a second user requesting the same title joins it rather than
        // being blocked. Only the same user re-requesting an active item is rejected.
        var alreadyMine = await _db.MediaRequests.AnyAsync(r =>
            r.MediaId == mediaId && r.MediaType == mediaType &&
            r.RequestedByUserId == userId &&
            r.Status != RequestStatus.Cancelled && r.Status != RequestStatus.Rejected);
        if (alreadyMine) return new MediaRequestResult { Success = false, ErrorMessage = "You've already requested this title." };

        var alreadyAvailable = await _db.MediaRequests.AnyAsync(r =>
            r.MediaId == mediaId && r.MediaType == mediaType && r.Status == RequestStatus.Available);
        if (alreadyAvailable) return new MediaRequestResult { Success = false, ErrorMessage = "This title is already available." };

        if (!isAdmin && !await CheckLimitsCoreAsync(userId, mediaType))
            return new MediaRequestResult { Success = false, ErrorMessage = "You've reached your request limit for this media type." };

        // Admins, and users flagged AutoApprove, skip the pending queue.
        var autoApprove = isAdmin || await _db.UserProfiles
            .Where(p => p.UserId == userId).Select(p => p.AutoApprove).FirstOrDefaultAsync();

        // Enrich with metadata for nice UI
        string title = $"Item #{mediaId}";
        string? poster = null;
        try
        {
            var details = await _metadata.GetDetailsAsync(mediaId, mediaType);
            if (details is not null)
            {
                title = string.IsNullOrWhiteSpace(details.Title) ? title : details.Title;
                poster = details.PosterUrl;
            }
        }
        catch { /* best-effort */ }

        var entity = new MediaRequestEntity
        {
            MediaId = mediaId,
            MediaType = mediaType,
            Title = title,
            PosterUrl = poster,
            Status = autoApprove ? RequestStatus.Approved : RequestStatus.Pending,
            RequestedAt = DateTime.UtcNow,
            ApprovedAt = autoApprove ? DateTime.UtcNow : null,
            RequestedBy = username,
            RequestedByUserId = userId,
            // TV selection: episodes > seasons > whole series. Movies are always "all".
            RequestAllSeasons = mediaType == MediaType.TvShow
                ? (episodes is not { Count: > 0 } && seasons is not { Count: > 0 } && allSeasons)
                : true,
            RequestedSeasonsCsv = mediaType == MediaType.TvShow && seasons is { Count: > 0 }
                ? string.Join(",", seasons.Distinct().OrderBy(x => x))
                : null,
            RequestedEpisodesCsv = mediaType == MediaType.TvShow && episodes is { Count: > 0 }
                ? string.Join(",", episodes.Distinct().OrderBy(e => e.season).ThenBy(e => e.episode).Select(e => $"S{e.season}E{e.episode}"))
                : null,
            Monitored = mediaType == MediaType.TvShow && monitored
        };
        _db.MediaRequests.Add(entity);
        var saved = await _db.SaveChangesAsync() > 0;
        if (saved)
        {
            var dto = ToDto(entity);
            if (autoApprove)
            {
                // Straight to approved: notify + hand off to fulfillment (if a downloader is wired up).
                await _notify.RequestApprovedAsync(dto);
                if (_config.GetValue<bool>("Fulfillment:Enabled")) await _fulfillment.EnqueueAsync(dto);
            }
            else
            {
                await _notify.RequestCreatedAsync(dto);
            }
        }
        return new MediaRequestResult { Success = saved, RequestId = entity.Id, NewStatus = entity.Status };
    }

    /// <summary>A specific user's requests, newest first (used by the Discord bridge /request status).</summary>
    public async Task<List<MediaRequestDto>> GetRequestsForUserAsync(int userId, int take = 25)
    {
        var rows = await _db.MediaRequests
            .Where(r => r.RequestedByUserId == userId)
            .OrderByDescending(r => r.RequestedAt)
            .Take(take)
            .ToListAsync();
        return rows.Select(ToDto).ToList();
    }

    private static MediaRequestDto ToDto(MediaRequestEntity r) => new()
    {
        Id = r.Id,
        MediaId = r.MediaId,
        MediaType = r.MediaType,
        Title = r.Title,
        PosterUrl = r.PosterUrl,
        Status = r.Status,
        RequestedAt = r.RequestedAt,
        ApprovedAt = r.ApprovedAt,
        AvailableAt = r.AvailableAt,
        DenialReason = r.DenialReason,
        RequestNote = r.RequestNote,
        RequestAllSeasons = r.RequestAllSeasons,
            RequestedEpisodesCsv = r.RequestedEpisodesCsv,
            Monitored = r.Monitored,
            ExternalId = r.ExternalId,
            ExternalSource = r.ExternalSource,
        RequestedByUserId = r.RequestedByUserId ?? 0,
        RequestedByUsername = r.RequestedBy ?? string.Empty,
        RequestedSeasons = string.IsNullOrWhiteSpace(r.RequestedSeasonsCsv)
            ? new List<int>()
            : r.RequestedSeasonsCsv.Split(',').Select(s => int.TryParse(s, out var n) ? (int?)n : null).Where(n => n.HasValue).Select(n => n!.Value).ToList()
    };

    public async Task<bool> RemoveFromWatchlistAsync(int mediaId, MediaType mediaType)
    {
        var (username, _) = await GetUserAsync();
        var item = await _db.Watchlist.FirstOrDefaultAsync(w => w.MediaId == mediaId && w.MediaType == mediaType && w.Username == username);
        if (item == null) return false;
        _db.Watchlist.Remove(item);
        return await _db.SaveChangesAsync() > 0;
    }

    public async Task<bool> CheckRequestLimitsAsync(MediaType mediaType)
    {
        var (username, isAdmin) = await GetUserAsync();
        if (isAdmin) return true;
        var userId = await _db.Users.Where(u => u.Username == username).Select(u => (int?)u.Id).FirstOrDefaultAsync();
        if (userId is null) return false;
        return await CheckLimitsCoreAsync(userId.Value, mediaType);
    }

    private async Task<bool> CheckLimitsCoreAsync(int userId, MediaType mediaType)
    {
        var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
        int? limit = mediaType switch
        {
            MediaType.Movie => profile?.MovieRequestLimit,
            MediaType.TvShow => profile?.TvRequestLimit,
            MediaType.Music => profile?.MusicRequestLimit,
            _ => null
        };
        if (limit is null) return true; // null => unlimited

        var active = await _db.MediaRequests.CountAsync(r =>
            r.RequestedByUserId == userId && r.MediaType == mediaType &&
            r.Status != RequestStatus.Cancelled && r.Status != RequestStatus.Rejected);
        return active < limit.Value;
    }

    public async Task<bool> ApproveRequestAsync(int requestId, string? note = null)
    {
        var (_, isAdmin) = await GetUserAsync();
        if (!isAdmin) return false;
        return await ApproveCoreAsync(requestId, note);
    }

    /// <summary>Approve without a cookie auth check — the caller (e.g. Discord bridge) has already verified admin.</summary>
    public Task<bool> ApproveRequestAsAdminAsync(int requestId, string? note = null) => ApproveCoreAsync(requestId, note);

    private async Task<bool> ApproveCoreAsync(int requestId, string? note)
    {
        var req = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (req is null) return false;
        req.Status = RequestStatus.Approved;
        req.ApprovedAt = DateTime.UtcNow;
        req.DenialReason = null;
        if (!string.IsNullOrWhiteSpace(note)) req.RequestNote = note;
        var ok = await _db.SaveChangesAsync() > 0;
        if (ok)
        {
            var dto = ToDto(req);
            await _notify.RequestApprovedAsync(dto);
            // Hand off to the fulfillment pipeline when a downloader is wired up.
            if (_config.GetValue<bool>("Fulfillment:Enabled"))
                await _fulfillment.EnqueueAsync(dto);
        }
        return ok;
    }

    public async Task<bool> DenyRequestAsync(int requestId, string reason)
    {
        var (_, isAdmin) = await GetUserAsync();
        if (!isAdmin) return false;
        return await DenyCoreAsync(requestId, reason);
    }

    /// <summary>Deny without a cookie auth check — the caller has already verified admin.</summary>
    public Task<bool> DenyRequestAsAdminAsync(int requestId, string reason) => DenyCoreAsync(requestId, reason);

    private async Task<bool> DenyCoreAsync(int requestId, string reason)
    {
        var req = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (req is null) return false;
        req.Status = RequestStatus.Rejected;
        req.DenialReason = reason;
        var ok = await _db.SaveChangesAsync() > 0;
        if (ok) await _notify.RequestRejectedAsync(ToDto(req));
        return ok;
    }

    public async Task<bool> MarkAvailableAsync(int requestId)
    {
        var (_, isAdmin) = await GetUserAsync();
        if (!isAdmin) return false;
        var req = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (req is null) return false;
        req.Status = RequestStatus.Available;
        req.AvailableAt = DateTime.UtcNow;
        var ok = await _db.SaveChangesAsync() > 0;
        if (ok)
        {
            var dto = new MediaRequestDto
            {
                Id = req.Id,
                MediaId = req.MediaId,
                MediaType = req.MediaType,
                Title = req.Title,
                PosterUrl = req.PosterUrl,
                Status = req.Status,
                RequestedAt = req.RequestedAt,
                ApprovedAt = req.ApprovedAt,
                AvailableAt = req.AvailableAt,
                RequestedByUserId = req.RequestedByUserId ?? 0,
                RequestedByUsername = req.RequestedBy ?? string.Empty
            };
            await _notify.RequestAvailableAsync(dto);
        }
        return ok;
    }

    public async Task<Dictionary<string, RequestStatus>> GetMyRequestStatusesAsync(IEnumerable<(int mediaId, MediaType mediaType)> items)
    {
        var (username, isAdmin) = await GetUserAsync();
        var ids = items.ToList();
        if (ids.Count == 0) return new();
        var mediaIds = ids.Select(i => i.mediaId).Distinct().ToList();
        // Query by mediaId only (narrow), then match exact (mediaId, mediaType) pairs in memory so a
        // movie can't inherit a same-id show's status (the old query cross-joined ids × types).
        var wanted = ids.Select(i => (i.mediaId, i.mediaType)).ToHashSet();
        IQueryable<MediaRequestEntity> q = _db.MediaRequests.Where(r => mediaIds.Contains(r.MediaId));
        if (!isAdmin)
            q = q.Where(r => r.RequestedBy == username);
        var list = (await q
            .Select(r => new { r.MediaId, r.MediaType, r.Status })
            .ToListAsync())
            .Where(r => wanted.Contains((r.MediaId, r.MediaType)));
        var dict = new Dictionary<string, RequestStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in list)
        {
            var key = $"{row.MediaType}:{row.MediaId}";
            // Use the most advanced status if multiple requests exist
            if (dict.TryGetValue(key, out var existing))
            {
                var best = BestStatus(existing, row.Status);
                dict[key] = best;
            }
            else dict[key] = row.Status;
        }
        return dict;
    }

    private static RequestStatus BestStatus(RequestStatus a, RequestStatus b)
    {
        // Simple precedence: Available > Approved > Pending > Cancelled/Rejected > None
        int Rank(RequestStatus s) => s switch
        {
            RequestStatus.Available => 5,
            RequestStatus.Approved => 4,
            RequestStatus.Pending => 3,
            RequestStatus.Cancelled => 2,
            RequestStatus.Rejected => 1,
            _ => 0
        };
        return Rank(a) >= Rank(b) ? a : b;
    }
}
