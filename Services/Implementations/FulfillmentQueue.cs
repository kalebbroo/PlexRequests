using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// Database-backed <see cref="IFulfillmentQueue"/>. SQLite is single-writer, so claims are safe
/// without extra locking at our scale; swap for Redis/RabbitMQ later without touching callers.
/// </summary>
public class FulfillmentQueue(AppDbContext db, IMediaMetadataProvider metadata) : IFulfillmentQueue
{
    private readonly AppDbContext _db = db;
    private readonly IMediaMetadataProvider _metadata = metadata;

    public async Task EnqueueAsync(MediaRequestDto request)
    {
        var active = await _db.FulfillmentJobs.AnyAsync(j =>
            j.MediaRequestId == request.Id &&
            j.Status != FulfillmentStatus.Completed &&
            j.Status != FulfillmentStatus.Failed &&
            j.Status != FulfillmentStatus.Cancelled);
        if (active) return;

        // Resolve the IMDb id up front so the downloader (EZTV/YTS are keyed by IMDb) needs no TMDb key.
        string? imdbId = null;
        try { imdbId = await _metadata.GetImdbIdAsync(request.MediaId, request.MediaType); }
        catch { /* best-effort; downloader can still try by title/year */ }

        _db.FulfillmentJobs.Add(new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            Title = request.Title,
            Year = null,
            TmdbId = request.MediaId, // MediaId is the TMDb id for the default provider
            ImdbId = imdbId,
            RequestedSeasonsCsv = request.RequestedSeasons.Count > 0 ? string.Join(",", request.RequestedSeasons) : null,
            Quality = request.PreferredQuality,
            Status = FulfillmentStatus.Queued,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    public async Task<List<FulfillmentJobDto>> ClaimNextAsync(string workerId, int max = 1)
    {
        if (max < 1) max = 1;
        var jobs = await _db.FulfillmentJobs
            .Where(j => j.Status == FulfillmentStatus.Queued)
            .OrderBy(j => j.CreatedAt)
            .Take(max)
            .ToListAsync();

        var now = DateTime.UtcNow;
        foreach (var j in jobs)
        {
            j.Status = FulfillmentStatus.Claimed;
            j.ClaimedBy = workerId;
            j.ClaimedAt = now;
            j.LastUpdatedAt = now;
            j.Attempts++;
        }
        if (jobs.Count > 0) await _db.SaveChangesAsync();
        return jobs.Select(Map).ToList();
    }

    public async Task<bool> ReportProgressAsync(int jobId, int progress)
    {
        var j = await _db.FulfillmentJobs.FirstOrDefaultAsync(x => x.Id == jobId);
        if (j is null) return false;
        j.Status = FulfillmentStatus.Downloading;
        j.Progress = Math.Clamp(progress, 0, 100);
        j.LastUpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task MarkCompletedAsync(int mediaRequestId)
    {
        var j = await LatestJobAsync(mediaRequestId);
        if (j is null) return;
        j.Status = FulfillmentStatus.Completed;
        j.Progress = 100;
        j.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task MarkFailedAsync(int mediaRequestId, string reason)
    {
        var j = await LatestJobAsync(mediaRequestId);
        if (j is null) return;
        j.Status = FulfillmentStatus.Failed;
        j.LastError = reason.Length > 2000 ? reason[..2000] : reason;
        j.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private Task<FulfillmentJobEntity?> LatestJobAsync(int mediaRequestId) =>
        _db.FulfillmentJobs
            .Where(x => x.MediaRequestId == mediaRequestId)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync();

    private static FulfillmentJobDto Map(FulfillmentJobEntity j) => new()
    {
        Id = j.Id,
        MediaRequestId = j.MediaRequestId,
        MediaId = j.MediaId,
        MediaType = j.MediaType,
        Title = j.Title,
        Year = j.Year,
        TmdbId = j.TmdbId,
        ImdbId = j.ImdbId,
        TvdbId = j.TvdbId,
        RequestedSeasons = string.IsNullOrWhiteSpace(j.RequestedSeasonsCsv)
            ? new List<int>()
            : j.RequestedSeasonsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n : (int?)null)
                .Where(n => n.HasValue).Select(n => n!.Value).ToList(),
        Quality = j.Quality,
        Status = j.Status,
        Attempts = j.Attempts,
        Progress = j.Progress
    };
}
