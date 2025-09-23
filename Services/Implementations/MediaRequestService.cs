using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;

namespace PlexRequestsHosted.Services.Implementations;

public class MediaRequestService(AppDbContext db, AuthenticationStateProvider authStateProvider, IMediaMetadataProvider metadataProvider) : IMediaRequestService
{
    private readonly AppDbContext _db = db;
    private readonly AuthenticationStateProvider _auth = authStateProvider;
    private readonly IMediaMetadataProvider _metadata = metadataProvider;

    private async Task<(string username, bool isAdmin)> GetUserAsync()
    {
        var state = await _auth.GetAuthenticationStateAsync();
        var user = state.User;
        var name = user.Identity?.Name ?? "";
        var isAdmin = user.IsInRole("Admin");
        return (name, isAdmin);
    }

    public async Task<bool> AddToWatchlistAsync(int mediaId)
    {
        var (username, _) = await GetUserAsync();
        if (string.IsNullOrWhiteSpace(username)) return false;
        _db.Watchlist.Add(new WatchlistItemEntity { MediaId = mediaId, Username = username });
        return await _db.SaveChangesAsync() > 0;
    } // TODO: Validate media exists in metadata provider before adding

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
    } // TODO: Filter by current user instead of hardcoded "demo"

    public async Task<List<MediaCardDto>> GetWatchlistAsync()
    {
        var (username, _) = await GetUserAsync();
        var list = await _db.Watchlist
            .Where(w => w.Username == username)
            .OrderByDescending(w => w.AddedAt)
            .Select(w => new MediaCardDto { Id = w.MediaId, Title = $"Item #{w.MediaId}", RequestStatus = RequestStatus.None })
            .ToListAsync();
        return list;
    } // TODO: Populate MediaCardDto with real data from metadata provider

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
            RequestedSeasons = string.IsNullOrWhiteSpace(r.RequestedSeasonsCsv) ? new List<int>() : r.RequestedSeasonsCsv.Split(',').Select(s => int.TryParse(s, out var n) ? n : (int?)null).Where(n => n.HasValue).Select(n => n!.Value).ToList()
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
                RequestedByUsername = r.RequestedBy ?? string.Empty
            });
        }
        if (changed)
        {
            try { await _db.SaveChangesAsync(); } catch { /* non-fatal */ }
        }
        return new PagedResult<MediaRequestDto> { Items = items, TotalCount = total, PageNumber = filter.PageNumber, PageSize = filter.PageSize };
    } // TODO: Add user filtering and admin permissions

    public async Task<bool> IsInWatchlistAsync(int mediaId)
    {
        var (username, _) = await GetUserAsync();
        return await _db.Watchlist.AnyAsync(w => w.MediaId == mediaId && w.Username == username);
    } // TODO: Filter by current user

    public async Task<MediaRequestResult> RequestMediaAsync(int mediaId, MediaType mediaType)
    {
        var (username, _) = await GetUserAsync();
        if (string.IsNullOrWhiteSpace(username)) return new MediaRequestResult { Success = false, ErrorMessage = "Not authenticated" };

        var exists = await _db.MediaRequests.AnyAsync(r => r.MediaId == mediaId && r.MediaType == mediaType && r.Status != RequestStatus.Cancelled);
        if (exists) return new MediaRequestResult { Success = false, ErrorMessage = "Already requested" };

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
            RequestedBy = username
        };
        _db.MediaRequests.Add(entity);
        var saved = await _db.SaveChangesAsync() > 0;
        return new MediaRequestResult { Success = saved, RequestId = entity.Id, NewStatus = entity.Status };
    } // TODO: Implement request limits and validation

    public async Task<bool> RemoveFromWatchlistAsync(int mediaId)
    {
        var (username, _) = await GetUserAsync();
        var item = await _db.Watchlist.FirstOrDefaultAsync(w => w.MediaId == mediaId && w.Username == username);
        if (item == null) return false;
        _db.Watchlist.Remove(item);
        return await _db.SaveChangesAsync() > 0;
    } // TODO: Filter by current user

    public Task<bool> CheckRequestLimitsAsync(MediaType mediaType)
        => Task.FromResult(true); // TODO: Implement actual request limits logic

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
        return await _db.SaveChangesAsync() > 0;
    }

    public async Task<bool> DenyRequestAsync(int requestId, string reason)
    {
        var (_, isAdmin) = await GetUserAsync();
        if (!isAdmin) return false;
        var req = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (req is null) return false;
        req.Status = RequestStatus.Rejected;
        req.DenialReason = reason;
        return await _db.SaveChangesAsync() > 0;
    }

    public async Task<bool> MarkAvailableAsync(int requestId)
    {
        var (_, isAdmin) = await GetUserAsync();
        if (!isAdmin) return false;
        var req = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (req is null) return false;
        req.Status = RequestStatus.Available;
        req.AvailableAt = DateTime.UtcNow;
        return await _db.SaveChangesAsync() > 0;
    }

    public async Task<Dictionary<string, RequestStatus>> GetMyRequestStatusesAsync(IEnumerable<(int mediaId, MediaType mediaType)> items)
    {
        var (username, isAdmin) = await GetUserAsync();
        var ids = items.ToList();
        if (ids.Count == 0) return new();
        var mediaIds = ids.Select(i => i.mediaId).Distinct().ToList();
        var types = ids.Select(i => i.mediaType).Distinct().ToList();
        IQueryable<MediaRequestEntity> q = _db.MediaRequests.Where(r => mediaIds.Contains(r.MediaId) && types.Contains(r.MediaType));
        if (!isAdmin)
            q = q.Where(r => r.RequestedBy == username);
        var list = await q
            .Select(r => new { r.MediaId, r.MediaType, r.Status })
            .ToListAsync();
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
