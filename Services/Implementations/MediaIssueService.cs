using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

public interface IMediaIssueService
{
    Task<bool> ReportIssueAsync(int mediaId, MediaType mediaType, string title, string? posterUrl, string reason, string? detail, int? season = null, int? episode = null);
    Task<List<MediaIssueDto>> GetIssuesAsync(bool openOnly = true);
    Task<int> GetOpenCountAsync();
    Task<bool> ResolveIssueAsync(int issueId);
    Task<bool> DismissIssueAsync(int issueId);
    /// <summary>Admin: force a re-download for the reported title (bypasses the on-Plex dedup) and resolves the issue.</summary>
    Task<bool> RequestRedownloadAsync(int issueId);
}

public class MediaIssueService(
    AppDbContext db,
    AuthenticationStateProvider authProvider,
    IFulfillmentQueue fulfillment,
    IConfiguration config) : IMediaIssueService
{
    public async Task<bool> ReportIssueAsync(int mediaId, MediaType mediaType, string title, string? posterUrl, string reason, string? detail, int? season = null, int? episode = null)
    {
        var (userId, username) = await CurrentUserAsync();
        db.MediaIssues.Add(new MediaIssueEntity
        {
            MediaId = mediaId,
            MediaType = mediaType,
            Title = title,
            PosterUrl = posterUrl,
            ReportedByUserId = userId,
            ReportedBy = username,
            Reason = string.IsNullOrWhiteSpace(reason) ? "Other" : reason,
            Detail = detail,
            SeasonNumber = season,
            EpisodeNumber = episode,
            Status = IssueStatus.Open,
            CreatedAt = DateTime.UtcNow
        });
        return await db.SaveChangesAsync() > 0;
    }

    public async Task<List<MediaIssueDto>> GetIssuesAsync(bool openOnly = true)
    {
        var q = db.MediaIssues.AsNoTracking().AsQueryable();
        if (openOnly) q = q.Where(i => i.Status == IssueStatus.Open);
        return await q.OrderByDescending(i => i.CreatedAt).Select(i => ToDto(i)).ToListAsync();
    }

    public Task<int> GetOpenCountAsync() => db.MediaIssues.CountAsync(i => i.Status == IssueStatus.Open);

    public async Task<bool> ResolveIssueAsync(int issueId) => await CloseAsync(issueId, IssueStatus.Resolved);
    public async Task<bool> DismissIssueAsync(int issueId) => await CloseAsync(issueId, IssueStatus.Dismissed);

    private async Task<bool> CloseAsync(int issueId, IssueStatus status)
    {
        var issue = await db.MediaIssues.FirstOrDefaultAsync(i => i.Id == issueId);
        if (issue is null) return false;
        var (_, username) = await CurrentUserAsync();
        issue.Status = status;
        issue.ResolvedAt = DateTime.UtcNow;
        issue.ResolvedBy = username;
        return await db.SaveChangesAsync() > 0;
    }

    public async Task<bool> RequestRedownloadAsync(int issueId)
    {
        var issue = await db.MediaIssues.FirstOrDefaultAsync(i => i.Id == issueId);
        if (issue is null) return false;

        // Create an approved request scoped to what was reported, then force-enqueue it (re-fetch even
        // though the content is on Plex). It flows through the normal pipeline -> Available on completion.
        var episodesCsv = issue.SeasonNumber is int s && issue.EpisodeNumber is int e ? $"S{s}E{e}" : null;
        var seasonsCsv = episodesCsv is null && issue.SeasonNumber is int so ? so.ToString() : null;

        var req = new MediaRequestEntity
        {
            MediaId = issue.MediaId,
            MediaType = issue.MediaType,
            Title = issue.Title,
            PosterUrl = issue.PosterUrl,
            Status = RequestStatus.Approved,
            RequestedAt = DateTime.UtcNow,
            ApprovedAt = DateTime.UtcNow,
            RequestedBy = issue.ReportedBy,
            RequestedByUserId = issue.ReportedByUserId,
            RequestAllSeasons = seasonsCsv is null && episodesCsv is null,
            RequestedSeasonsCsv = seasonsCsv,
            RequestedEpisodesCsv = episodesCsv
        };
        db.MediaRequests.Add(req);
        await db.SaveChangesAsync();

        if (config.GetValue<bool>("Fulfillment:Enabled"))
            await fulfillment.EnqueueAsync(ToRequestDto(req), force: true);

        var (_, username) = await CurrentUserAsync();
        issue.Status = IssueStatus.Resolved;
        issue.ResolvedAt = DateTime.UtcNow;
        issue.ResolvedBy = username;
        await db.SaveChangesAsync();
        return true;
    }

    private async Task<(int? userId, string? username)> CurrentUserAsync()
    {
        var state = await authProvider.GetAuthenticationStateAsync();
        var username = state.User.Identity?.Name;
        int? userId = int.TryParse(state.User.FindFirst("user_id")?.Value, out var id) ? id : null;
        return (userId, username);
    }

    private static MediaIssueDto ToDto(MediaIssueEntity i) => new()
    {
        Id = i.Id, MediaId = i.MediaId, MediaType = i.MediaType, Title = i.Title, PosterUrl = i.PosterUrl,
        ReportedByUserId = i.ReportedByUserId, ReportedBy = i.ReportedBy, Reason = i.Reason, Detail = i.Detail,
        SeasonNumber = i.SeasonNumber, EpisodeNumber = i.EpisodeNumber, Status = i.Status,
        CreatedAt = i.CreatedAt, ResolvedAt = i.ResolvedAt
    };

    private static MediaRequestDto ToRequestDto(MediaRequestEntity r) => new()
    {
        Id = r.Id, MediaId = r.MediaId, MediaType = r.MediaType, Title = r.Title, PosterUrl = r.PosterUrl,
        Status = r.Status, RequestedAt = r.RequestedAt, ApprovedAt = r.ApprovedAt,
        RequestedByUserId = r.RequestedByUserId ?? 0, RequestedByUsername = r.RequestedBy ?? string.Empty,
        RequestAllSeasons = r.RequestAllSeasons, RequestedEpisodesCsv = r.RequestedEpisodesCsv,
        RequestedSeasons = string.IsNullOrWhiteSpace(r.RequestedSeasonsCsv) ? new() :
            r.RequestedSeasonsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out var n) ? n : -1).Where(n => n >= 0).ToList()
    };
}
