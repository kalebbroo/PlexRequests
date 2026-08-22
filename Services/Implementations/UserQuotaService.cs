using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>Canonical rolling-quota calculator used for both enforcement and display.</summary>
public sealed class UserQuotaService(AppDbContext db) : IUserQuotaService
{
    public async Task<UserQuotaOverviewDto> GetUsageAsync(int userId, UserAccessSnapshot access)
    {
        var results = await GetUsageAsync([access]);
        return results.TryGetValue(userId, out var usage) ? usage : Empty(access);
    }

    public async Task<IReadOnlyDictionary<int, UserQuotaOverviewDto>> GetUsageAsync(
        IReadOnlyCollection<UserAccessSnapshot> users)
    {
        if (users.Count == 0) return new Dictionary<int, UserQuotaOverviewDto>();

        var now = DateTime.UtcNow;
        var earliest = users.Min(x => now.AddDays(-Math.Max(
            x.MovieRequestLimitDays, Math.Max(x.TvRequestLimitDays, x.MusicRequestLimitDays))));
        var ids = users.Select(x => x.UserId).Distinct().ToArray();
        var requests = await db.MediaRequests.AsNoTracking()
            .Where(x => x.RequestedByUserId != null && ids.Contains(x.RequestedByUserId.Value)
                        && x.RequestedAt >= earliest
                        && x.Status != RequestStatus.Cancelled && x.Status != RequestStatus.Rejected)
            .Select(x => new QuotaRequest(x.RequestedByUserId!.Value, x.MediaType, x.Status, x.RequestedAt))
            .ToListAsync();

        return users.DistinctBy(x => x.UserId).ToDictionary(
            access => access.UserId,
            access => Build(access, requests, now));
    }

    public async Task<bool> HasCapacityAsync(int userId, MediaType mediaType, UserAccessSnapshot access)
    {
        if (access.IsAdmin) return true;
        var usage = await GetUsageAsync(userId, access);
        var quota = mediaType switch
        {
            MediaType.Movie => usage.Movie,
            MediaType.TvShow or MediaType.Anime => usage.Tv,
            MediaType.Music => usage.Music,
            _ => new RequestQuotaDto { Limit = 0, Used = 0, WindowDays = 1 }
        };
        return quota.Limit is null || quota.Used < quota.Limit.Value;
    }

    private static UserQuotaOverviewDto Build(
        UserAccessSnapshot access,
        IReadOnlyCollection<QuotaRequest> requests,
        DateTime now) => new()
    {
        Movie = Quota(access.MovieRequestLimit, access.MovieRequestLimitDays,
            requests.Count(x => x.UserId == access.UserId && CountsTowardQuota(x.MediaType, x.Status, x.RequestedAt, access, now)
                                && x.MediaType == MediaType.Movie)),
        Tv = Quota(access.TvRequestLimit, access.TvRequestLimitDays,
            requests.Count(x => x.UserId == access.UserId && CountsTowardQuota(x.MediaType, x.Status, x.RequestedAt, access, now)
                                && x.MediaType is MediaType.TvShow or MediaType.Anime)),
        Music = Quota(access.MusicRequestLimit, access.MusicRequestLimitDays,
            requests.Count(x => x.UserId == access.UserId && CountsTowardQuota(x.MediaType, x.Status, x.RequestedAt, access, now)
                                && x.MediaType == MediaType.Music))
    };

    private static UserQuotaOverviewDto Empty(UserAccessSnapshot access) => new()
    {
        Movie = Quota(access.MovieRequestLimit, access.MovieRequestLimitDays, 0),
        Tv = Quota(access.TvRequestLimit, access.TvRequestLimitDays, 0),
        Music = Quota(access.MusicRequestLimit, access.MusicRequestLimitDays, 0)
    };

    private static RequestQuotaDto Quota(int? limit, int days, int used) =>
        new() { Limit = limit, WindowDays = days, Used = used };

    internal static bool CountsTowardQuota(MediaType mediaType, RequestStatus status, DateTime requestedAt,
        UserAccessSnapshot access, DateTime now) =>
        status is not (RequestStatus.Cancelled or RequestStatus.Rejected)
        && QuotaExpiresAt(mediaType, requestedAt, access) is DateTime expires
        && expires >= now;

    internal static DateTime? QuotaExpiresAt(MediaType mediaType, DateTime requestedAt, UserAccessSnapshot access) =>
        mediaType switch
        {
            MediaType.Movie => requestedAt.AddDays(access.MovieRequestLimitDays),
            MediaType.TvShow or MediaType.Anime => requestedAt.AddDays(access.TvRequestLimitDays),
            MediaType.Music => requestedAt.AddDays(access.MusicRequestLimitDays),
            _ => null
        };

    private sealed record QuotaRequest(int UserId, MediaType MediaType, RequestStatus Status, DateTime RequestedAt);
}
