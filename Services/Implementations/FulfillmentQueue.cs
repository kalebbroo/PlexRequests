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

        var seasonsCsv = request.RequestedSeasons.Count > 0 ? string.Join(",", request.RequestedSeasons) : null;
        var episodesCsv = request.RequestedEpisodesCsv;

        // Never re-download content already on Plex: narrow a TV request to only the missing seasons/
        // episodes. If nothing is missing, don't enqueue at all — the reconciliation service marks the
        // request Available. (Movies fall through unchanged.)
        if (request.MediaType == MediaType.TvShow)
        {
            var (s, e, hasTarget) = await ComputeMissingTvTargetsAsync(request);
            if (!hasTarget) return;   // everything already on Plex
            seasonsCsv = s;
            episodesCsv = e;
        }

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
            RequestedSeasonsCsv = seasonsCsv,
            RequestedEpisodesCsv = episodesCsv,
            Quality = request.PreferredQuality,
            Status = FulfillmentStatus.Queued,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Given a TV request, returns the seasons/episodes CSVs to actually fetch (only what's missing
    /// from Plex) and whether there's anything to fetch at all. A season is considered satisfied when
    /// Plex already has at least its TMDB episode count.
    /// </summary>
    private async Task<(string? seasonsCsv, string? episodesCsv, bool hasTarget)> ComputeMissingTvTargetsAsync(MediaRequestDto request)
    {
        var onPlex = await GetPlexSeasonsAsync(request.MediaId);   // season -> available episode numbers
        var detail = await _metadata.GetDetailsAsync(request.MediaId, MediaType.TvShow);

        // Episode-level request: keep only episodes not already on Plex.
        if (!string.IsNullOrWhiteSpace(request.RequestedEpisodesCsv))
        {
            var wanted = ParseEpisodes(request.RequestedEpisodesCsv);   // reuses the existing S/E parser
            var missing = wanted.Where(w => !(onPlex.TryGetValue(w.Season, out var set) && set.Contains(w.Episode))).ToList();
            if (missing.Count == 0) return (null, null, false);
            return (null, string.Join(",", missing.Select(m => $"S{m.Season}E{m.Episode}")), true);
        }

        // Which seasons are in scope: an explicit list, else (whole series) every season TMDB knows.
        var scopeSeasons = detail?.Seasons?.Where(s => s.SeasonNumber > 0);
        if (request.RequestedSeasons.Count > 0)
            scopeSeasons = scopeSeasons?.Where(s => request.RequestedSeasons.Contains(s.SeasonNumber));
        var seasonsList = scopeSeasons?.ToList();

        // If we can't resolve the season list (metadata miss), don't guess — enqueue as originally asked.
        if (seasonsList is null || seasonsList.Count == 0)
        {
            var csv = request.RequestedSeasons.Count > 0 ? string.Join(",", request.RequestedSeasons) : null;
            return (csv, null, true);
        }

        var missingSeasons = new List<int>();
        foreach (var s in seasonsList)
        {
            var plexCount = onPlex.TryGetValue(s.SeasonNumber, out var set) ? set.Count : 0;
            // Complete when Plex has >= the TMDB episode count (or, if TMDB count unknown, any episodes).
            var complete = plexCount > 0 && (s.EpisodeCount <= 0 || plexCount >= s.EpisodeCount);
            // Only fetch seasons that have actually started airing; future seasons are the monitor's job,
            // so a whole-series request of a caught-up show doesn't spawn a doomed job for an unaired season.
            var aired = !s.AirDate.HasValue || s.AirDate.Value.Date <= DateTime.UtcNow.Date;
            if (!complete && aired) missingSeasons.Add(s.SeasonNumber);
        }
        if (missingSeasons.Count == 0) return (null, null, false);
        return (string.Join(",", missingSeasons), null, true);
    }

    // season -> set of episode numbers on Plex (from the DB availability index: tmdb -> ratingKey -> seasons).
    private async Task<Dictionary<int, HashSet<int>>> GetPlexSeasonsAsync(int tmdbId)
    {
        var result = new Dictionary<int, HashSet<int>>();
        var ratingKey = await _db.PlexMappings.Where(m => m.ExternalKey == $"tmdb:{tmdbId}").Select(m => m.RatingKey).FirstOrDefaultAsync();
        if (string.IsNullOrEmpty(ratingKey)) return result;
        var seasons = await _db.PlexSeasonAvailability.Where(s => s.ShowRatingKey == ratingKey).ToListAsync();
        foreach (var s in seasons)
            result[s.SeasonNumber] = s.AvailableEpisodesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out var n) ? n : -1).Where(n => n >= 0).ToHashSet();
        return result;
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

    // Parse "S1E1,S2E5" into episode targets.
    private static List<EpisodeRef> ParseEpisodes(string? csv)
    {
        var list = new List<EpisodeRef>();
        if (string.IsNullOrWhiteSpace(csv)) return list;
        foreach (var tok in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(tok, @"^[Ss](\d+)[Ee](\d+)$");
            if (m.Success) list.Add(new EpisodeRef { Season = int.Parse(m.Groups[1].Value), Episode = int.Parse(m.Groups[2].Value) });
        }
        return list;
    }

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
        RequestedEpisodes = ParseEpisodes(j.RequestedEpisodesCsv),
        Quality = j.Quality,
        Status = j.Status,
        Attempts = j.Attempts,
        Progress = j.Progress
    };
}
