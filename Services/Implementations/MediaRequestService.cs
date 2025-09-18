using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;

namespace PlexRequestsHosted.Services.Implementations;

public class MediaRequestService(AppDbContext db) : IMediaRequestService
{
    private readonly AppDbContext _db = db;

    public Task<bool> AddToWatchlistAsync(int mediaId)
    {
        _db.Watchlist.Add(new WatchlistItemEntity { MediaId = mediaId, Username = "demo" });
        return _db.SaveChangesAsync().ContinueWith(t => t.Result > 0);
    }

    public Task<bool> CancelRequestAsync(int requestId)
    {
        var req = _db.MediaRequests.FirstOrDefault(r => r.Id == requestId);
        if (req is null) return Task.FromResult(false);
        req.Status = RequestStatus.Cancelled;
        return _db.SaveChangesAsync().ContinueWith(t => t.Result > 0);
    }

    public Task<UserStatsDto> GetMyStatsAsync()
    {
        var stats = new UserStatsDto
        {
            TotalRequests = _db.MediaRequests.Count(),
            ApprovedRequests = _db.MediaRequests.Count(r => r.Status == RequestStatus.Approved),
            PendingRequests = _db.MediaRequests.Count(r => r.Status == RequestStatus.Pending),
            AvailableRequests = _db.MediaRequests.Count(r => r.Status == RequestStatus.Available),
            LastRequestDate = _db.MediaRequests.OrderByDescending(r => r.RequestedAt).Select(r => (DateTime?)r.RequestedAt).FirstOrDefault()
        };
        return Task.FromResult(stats);
    }

    public Task<List<MediaCardDto>> GetWatchlistAsync()
    {
        var list = _db.Watchlist
            .Where(w => w.Username == "demo")
            .OrderByDescending(w => w.AddedAt)
            .Select(w => new MediaCardDto { Id = w.MediaId, Title = $"Item #{w.MediaId}", RequestStatus = RequestStatus.None })
            .ToList();
        return Task.FromResult(list);
    }

    public Task<MediaRequestDto?> GetRequestByIdAsync(int id)
    {
        var r = _db.MediaRequests.FirstOrDefault(r => r.Id == id);
        return Task.FromResult(r == null ? null : new MediaRequestDto
        {
            Id = r.Id, MediaId = r.MediaId, MediaType = r.MediaType, Title = r.Title, Status = r.Status, RequestedAt = r.RequestedAt
        });
    }

    public Task<PagedResult<MediaRequestDto>> GetRequestsAsync(MediaFilterDto filter)
    {
        var query = _db.MediaRequests.OrderByDescending(r => r.RequestedAt);
        var total = query.Count();
        var items = query
            .Skip((filter.PageNumber - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(r => new MediaRequestDto
            {
                Id = r.Id, MediaId = r.MediaId, MediaType = r.MediaType, Title = r.Title, Status = r.Status, RequestedAt = r.RequestedAt
            })
            .ToList();
        return Task.FromResult(new PagedResult<MediaRequestDto> { Items = items, TotalCount = total, PageNumber = filter.PageNumber, PageSize = filter.PageSize });
    }

    public Task<bool> IsInWatchlistAsync(int mediaId) => Task.FromResult(_db.Watchlist.Any(w => w.MediaId == mediaId && w.Username == "demo"));

    public Task<MediaRequestResult> RequestMediaAsync(int mediaId, MediaType mediaType)
    {
        var exists = _db.MediaRequests.Any(r => r.MediaId == mediaId && r.MediaType == mediaType && r.Status != RequestStatus.Cancelled);
        if (exists) return Task.FromResult(new MediaRequestResult { Success = false, ErrorMessage = "Already requested" });

        var entity = new MediaRequestEntity
        {
            MediaId = mediaId,
            MediaType = mediaType,
            Title = $"Item #{mediaId}",
            Status = RequestStatus.Pending,
            RequestedAt = DateTime.UtcNow,
            RequestedBy = "demo"
        };
        _db.MediaRequests.Add(entity);
        return _db.SaveChangesAsync().ContinueWith(t => new MediaRequestResult { Success = t.Result > 0, RequestId = entity.Id, NewStatus = entity.Status });
    }

    public Task<bool> RemoveFromWatchlistAsync(int mediaId)
    {
        var item = _db.Watchlist.FirstOrDefault(w => w.MediaId == mediaId && w.Username == "demo");
        if (item == null) return Task.FromResult(false);
        _db.Watchlist.Remove(item);
        return _db.SaveChangesAsync().ContinueWith(t => t.Result > 0);
    }

    public Task<bool> CheckRequestLimitsAsync(MediaType mediaType)
        => Task.FromResult(true);
}
