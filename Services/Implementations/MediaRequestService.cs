using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;

namespace PlexRequestsHosted.Services.Implementations;

public class MediaRequestService(AppDbContext db, AuthenticationStateProvider authStateProvider, IMediaMetadataProvider metadataProvider, INotificationService notificationService) : IMediaRequestService
{
    private readonly AppDbContext _db = db;
    private readonly AuthenticationStateProvider _auth = authStateProvider;
    private readonly IMediaMetadataProvider _metadata = metadataProvider;
    private readonly INotificationService _notify = notificationService;

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

        // Get user ID from database
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        var userId = user?.Id;

        // Per-user duplicate check: a second user requesting the same title joins it rather than
        // being blocked. Only the same user re-requesting an active item is rejected.
        var alreadyMine = await _db.MediaRequests.AnyAsync(r =>
            r.MediaId == mediaId && r.MediaType == mediaType &&
            r.RequestedByUserId == userId &&
            r.Status != RequestStatus.Cancelled && r.Status != RequestStatus.Rejected);
        if (alreadyMine) return new MediaRequestResult { Success = false, ErrorMessage = "You've already requested this title." };

        // If it's already available on the server, no need to request again.
        var alreadyAvailable = await _db.MediaRequests.AnyAsync(r =>
            r.MediaId == mediaId && r.MediaType == mediaType && r.Status == RequestStatus.Available);
        if (alreadyAvailable) return new MediaRequestResult { Success = false, ErrorMessage = "This title is already available." };

        // Enforce per-user request limits (admins are exempt).
        if (!isAdmin && !await CheckRequestLimitsAsync(mediaType))
            return new MediaRequestResult { Success = false, ErrorMessage = "You've reached your request limit for this media type." };

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
            Status = RequestStatus.Pending,
            RequestedAt = DateTime.UtcNow,
            RequestedBy = username,
            RequestedByUserId = userId
        };
        _db.MediaRequests.Add(entity);
        var saved = await _db.SaveChangesAsync() > 0;
        if (saved)
        {
            var dto = new MediaRequestDto
            {
                Id = entity.Id,
                MediaId = entity.MediaId,
                MediaType = entity.MediaType,
                Title = entity.Title,
                PosterUrl = entity.PosterUrl,
                Status = entity.Status,
                RequestedAt = entity.RequestedAt,
                ApprovedAt = entity.ApprovedAt,
                AvailableAt = entity.AvailableAt,
                RequestedByUserId = entity.RequestedByUserId ?? 0,
                RequestedByUsername = entity.RequestedBy ?? string.Empty,
                RequestNote = entity.RequestNote,
                DenialReason = entity.DenialReason,
                RequestedSeasons = string.IsNullOrWhiteSpace(entity.RequestedSeasonsCsv) ? new List<int>() : entity.RequestedSeasonsCsv.Split(',').Select(s => int.TryParse(s, out var n) ? n : (int?)null).Where(n => n.HasValue).Select(n => n!.Value).ToList(),
                RequestAllSeasons = entity.RequestAllSeasons
            };
            await _notify.RequestCreatedAsync(dto);
        }
        return new MediaRequestResult { Success = saved, RequestId = entity.Id, NewStatus = entity.Status };
    } // TODO: Implement request limits and validation

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

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user is null) return false;

        var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == user.Id);
        int? limit = mediaType switch
        {
            MediaType.Movie => profile?.MovieRequestLimit,
            MediaType.TvShow => profile?.TvRequestLimit,
            MediaType.Music => profile?.MusicRequestLimit,
            _ => null
        };
        if (limit is null) return true; // null => unlimited

        // Count the user's active (not cancelled/rejected) requests of this type.
        var active = await _db.MediaRequests.CountAsync(r =>
            r.RequestedByUserId == user.Id && r.MediaType == mediaType &&
            r.Status != RequestStatus.Cancelled && r.Status != RequestStatus.Rejected);
        return active < limit.Value;
    }

    public async Task<bool> ApproveRequestAsync(int requestId, string? note = null)
    {
        var (_, isAdmin) = await GetUserAsync();
        if (!isAdmin) return false;
        var req = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (req is null) return false;
        req.Status = RequestStatus.Approved;
        req.ApprovedAt = DateTime.UtcNow;
        req.DenialReason = null;
        if (!string.IsNullOrWhiteSpace(note)) req.RequestNote = note;
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
                RequestedByUsername = req.RequestedBy ?? string.Empty,
                RequestNote = req.RequestNote
            };
            await _notify.RequestApprovedAsync(dto);
        }
        return ok;
    }

    public async Task<bool> DenyRequestAsync(int requestId, string reason)
    {
        var (_, isAdmin) = await GetUserAsync();
        if (!isAdmin) return false;
        var req = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (req is null) return false;
        req.Status = RequestStatus.Rejected;
        req.DenialReason = reason;
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
                RequestedByUsername = req.RequestedBy ?? string.Empty,
                DenialReason = req.DenialReason
            };
            await _notify.RequestRejectedAsync(dto);
        }
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
