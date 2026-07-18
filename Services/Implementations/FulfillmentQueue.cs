using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Jobs;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// Database-backed <see cref="IFulfillmentQueue"/>. SQLite is single-writer, so claims are safe
/// without extra locking at our scale; swap for Redis/RabbitMQ later without touching callers.
/// </summary>
public class FulfillmentQueue(AppDbContext db, IMediaMetadataProvider metadata, IQualityRuleService quality, ISeasonAvailabilityEvaluator seasonEvaluator) : IFulfillmentQueue
{
    private readonly AppDbContext _db = db;
    private readonly IMediaMetadataProvider _metadata = metadata;
    private readonly IQualityRuleService _quality = quality;
    private readonly ISeasonAvailabilityEvaluator _seasonEvaluator = seasonEvaluator;

    public async Task EnqueueAsync(MediaRequestDto request, bool force = false)
    {
        var active = await _db.FulfillmentJobs.AnyAsync(j =>
            j.MediaRequestId == request.Id &&
            j.Status != FulfillmentStatus.Completed &&
            j.Status != FulfillmentStatus.Failed &&
            j.Status != FulfillmentStatus.Cancelled);
        if (active) return;

        // Other active jobs for the SAME title from a DIFFERENT request (two users, or one user
        // requesting different seasons at different times) — never let two jobs re-download the same
        // content concurrently. Subtracted from what this job would target, below.
        var otherActive = await _db.FulfillmentJobs.Where(j =>
                j.MediaId == request.MediaId && j.MediaType == request.MediaType &&
                j.MediaRequestId != request.Id &&
                j.Status != FulfillmentStatus.Completed && j.Status != FulfillmentStatus.Failed && j.Status != FulfillmentStatus.Cancelled)
            .ToListAsync();

        if (request.MediaType != MediaType.TvShow && otherActive.Count > 0)
            return; // no sub-scope for movies/other media — one in-flight job for the title is enough

        var seasonsCsv = request.RequestedSeasons.Count > 0 ? string.Join(",", request.RequestedSeasons) : null;
        var episodesCsv = request.RequestedEpisodesCsv;
        List<SeasonTarget> seasonTargets = new();

        // Never re-download content already on Plex: narrow a TV request to only the missing seasons/
        // episodes. If nothing is missing, don't enqueue at all — the reconciliation service marks the
        // request Available. (Movies fall through unchanged.) A forced re-download skips this entirely.
        if (request.MediaType == MediaType.TvShow && !force)
        {
            var (s, e, targets, hasTarget) = await ComputeMissingTvTargetsAsync(request);
            if (!hasTarget) return;   // everything already on Plex
            seasonsCsv = s;
            episodesCsv = e;
            seasonTargets = targets;

            if (otherActive.Count > 0)
            {
                if (otherActive.Any(j => ExpandJobScope(j).coversEverything))
                    return; // another in-flight job already covers the whole show — nothing left to add

                var inFlightSeasons = new HashSet<int>();
                var inFlightEpisodes = new HashSet<(int season, int episode)>();
                foreach (var j in otherActive)
                {
                    var (js, je, _) = ExpandJobScope(j);
                    inFlightSeasons.UnionWith(js);
                    inFlightEpisodes.UnionWith(je);
                }

                if (!string.IsNullOrWhiteSpace(episodesCsv))
                {
                    var remaining = episodesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(tok =>
                        {
                            var m = Regex.Match(tok, @"^[Ss](\d+)[Ee](\d+)$");
                            if (!m.Success) return true;
                            var se = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
                            return !inFlightEpisodes.Contains(se) && !inFlightSeasons.Contains(se.Item1);
                        }).ToList();
                    if (remaining.Count == 0) return;
                    episodesCsv = string.Join(",", remaining);
                }
                else if (!string.IsNullOrWhiteSpace(seasonsCsv))
                {
                    var remainingSeasons = seasonsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(x => int.TryParse(x, out var n) ? n : -1)
                        .Where(n => n >= 0 && !inFlightSeasons.Contains(n))
                        .ToList();
                    if (remainingSeasons.Count == 0) return;
                    seasonsCsv = string.Join(",", remainingSeasons);
                    seasonTargets = seasonTargets.Where(t => remainingSeasons.Contains(t.Season)).ToList();
                }
            }
        }

        // Resolve the IMDb id + year up front so the downloader (EZTV/YTS are keyed by IMDb; year
        // disambiguates free-text searches and gates the ranker against wrong-year matches) needs no
        // further metadata lookups of its own.
        string? imdbId = null;
        List<string>? genres = null;
        int? year = null;
        bool isAnime = false;
        try
        {
            var detail = await _metadata.GetDetailsAsync(request.MediaId, request.MediaType);
            imdbId = detail?.ImdbId;
            genres = detail?.Genres;
            year = detail?.Year;
            isAnime = AnimeClassifier.IsAnime(detail?.Genres, detail?.Languages, detail?.Countries);
            if (string.IsNullOrEmpty(imdbId)) imdbId = await _metadata.GetImdbIdAsync(request.MediaId, request.MediaType);
        }
        catch { /* best-effort; downloader can still try by title/year */ }

        // Apply the admin quality rules (first matching override, else the default) instead of the
        // request's own preference, so quality is centrally controlled.
        var resolvedQuality = await _quality.ResolveQualityAsync(request.MediaType, request.MediaId, genres);

        _db.FulfillmentJobs.Add(new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            Title = request.Title,
            Year = year,
            TmdbId = request.MediaId, // MediaId is the TMDb id for the default provider
            ImdbId = imdbId,
            ExternalId = request.ExternalId,           // music/other-source id (TODO: downloader music support)
            ExternalSource = request.ExternalSource,
            RequestedSeasonsCsv = seasonsCsv,
            RequestedEpisodesCsv = episodesCsv,
            SeasonTargetsJson = seasonTargets.Count > 0 ? JsonSerializer.Serialize(seasonTargets) : null,
            Quality = resolvedQuality,
            GenresCsv = genres is { Count: > 0 } ? string.Join(",", genres) : null,
            IsAnime = isAnime,
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
    private async Task<(string? seasonsCsv, string? episodesCsv, List<SeasonTarget> seasonTargets, bool hasTarget)> ComputeMissingTvTargetsAsync(MediaRequestDto request)
    {
        // Episode-level request: keep only episodes not already on Plex. (Fan-out is precise via the CSV;
        // no SeasonTargets needed — the downloader uses RequestedEpisodes directly.)
        if (!string.IsNullOrWhiteSpace(request.RequestedEpisodesCsv))
        {
            var onPlex = await _seasonEvaluator.GetPlexEpisodesAsync(request.MediaId);
            var wanted = ParseEpisodes(request.RequestedEpisodesCsv);   // reuses the existing S/E parser
            var missing = wanted.Where(w => !(onPlex.TryGetValue(w.Season, out var set) && set.Contains(w.Episode))).ToList();
            if (missing.Count == 0) return (null, null, new(), false);
            return (null, string.Join(",", missing.Select(m => $"S{m.Season}E{m.Episode}")), new(), true);
        }

        // Per-season completeness (Plex episode count vs. TMDB's expected count), from the single shared
        // evaluator also used by PlexApiService.GetAvailableSeasonsAsync and the reconciliation service.
        var evaluation = await _seasonEvaluator.EvaluateAsync(request.MediaId);

        // Which seasons are in scope: an explicit list, else (whole series) every season TMDB knows.
        var scopeSeasons = evaluation.Values.AsEnumerable();
        if (request.RequestedSeasons.Count > 0)
            scopeSeasons = scopeSeasons.Where(s => request.RequestedSeasons.Contains(s.SeasonNumber));
        var seasonsList = scopeSeasons.ToList();

        // If we can't resolve the season list (metadata miss, or requested seasons TMDB doesn't know
        // about), don't guess — enqueue as originally asked. Without episode counts the downloader can't
        // fan out, so SeasonTargets stays empty (pack-only).
        if (seasonsList.Count == 0)
        {
            var csv = request.RequestedSeasons.Count > 0 ? string.Join(",", request.RequestedSeasons) : null;
            return (csv, null, new(), true);
        }

        var missingSeasons = new List<int>();
        var targets = new List<SeasonTarget>();
        foreach (var s in seasonsList)
        {
            // Only fetch seasons that have actually started airing; future seasons are the monitor's job,
            // so a whole-series request of a caught-up show doesn't spawn a doomed job for an unaired season.
            if (!s.Complete && s.Aired)
            {
                missingSeasons.Add(s.SeasonNumber);
                targets.Add(new SeasonTarget { Season = s.SeasonNumber, EpisodeCount = s.ExpectedCount, MissingEpisodes = s.MissingEpisodes });
            }
        }
        if (missingSeasons.Count == 0) return (null, null, new(), false);
        return (string.Join(",", missingSeasons), null, targets, true);
    }

    public async Task<List<FulfillmentJobDto>> ClaimNextAsync(string workerId, int max = 1)
    {
        if (max < 1) max = 1;
        var now0 = DateTime.UtcNow;
        // Claim Queued jobs whose retry backoff (if any) has elapsed. Deferred jobs aren't Queued, so they're
        // skipped here — the scheduler's MissingSearch job flips them back to Queued once they're due.
        var jobs = await _db.FulfillmentJobs
            .Where(j => j.Status == FulfillmentStatus.Queued && (j.NextRetryAt == null || j.NextRetryAt <= now0))
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

    public async Task MarkPartiallyCompletedAsync(int mediaRequestId, string reason)
    {
        var j = await LatestJobAsync(mediaRequestId);
        if (j is null) return;
        j.Status = FulfillmentStatus.PartiallyCompleted;
        j.LastError = reason.Length > 2000 ? reason[..2000] : reason;
        j.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<DeferResult> MarkDeferredAsync(int jobId, string reason)
    {
        var j = await _db.FulfillmentJobs.FirstOrDefaultAsync(x => x.Id == jobId);
        if (j is null) return new DeferResult(false, false, 0, null, false);

        var now = DateTime.UtcNow;
        j.DeferCount++;
        j.NextRetryAt = RetryBackoff.ComputeNextRetry(j.DeferCount, j.ReleaseDate, now);
        j.Status = FulfillmentStatus.Deferred;
        j.LastError = reason.Length > 2000 ? reason[..2000] : reason;
        j.LastUpdatedAt = now;
        j.ClaimedBy = null;          // release the claim so it's not seen as an active download
        j.Progress = 0;

        // Escalate to admins exactly once, after enough empty searches — never auto-fail.
        bool shouldEscalate = !j.Escalated && j.DeferCount >= RetryBackoff.EscalateAfterDeferrals;
        if (shouldEscalate) j.Escalated = true;

        await _db.SaveChangesAsync();
        return new DeferResult(true, j.IsUpgrade, j.DeferCount, j.NextRetryAt, shouldEscalate);
    }

    public async Task MarkUpgradeExhaustedAsync(int jobId)
    {
        var j = await _db.FulfillmentJobs.FirstOrDefaultAsync(x => x.Id == jobId);
        if (j is null) return;
        // The content is already available; an upgrade simply found nothing better. Close the job quietly
        // (not a failure) and stamp the request's cooldown so it's reconsidered on a later scan, not at once.
        j.Status = FulfillmentStatus.Cancelled;
        j.LastError = "No better-quality release found";
        j.CompletedAt = DateTime.UtcNow;
        var req = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == j.MediaRequestId);
        if (req is not null) req.LastUpgradeSearchAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<bool> EnqueueUpgradeAsync(MediaRequestDto request, Quality target, IReadOnlyList<string> replacePaths, IReadOnlyList<(int season, int episode)> episodes)
    {
        // Never run two upgrades for the same request at once.
        var activeUpgrade = await _db.FulfillmentJobs.AnyAsync(j =>
            j.MediaRequestId == request.Id && j.IsUpgrade &&
            j.Status != FulfillmentStatus.Completed && j.Status != FulfillmentStatus.Failed && j.Status != FulfillmentStatus.Cancelled);
        if (activeUpgrade) return false;

        // Copy identity fields (IMDb id / year / genres / anime flag) from the most recent real job so the
        // ranker can search without another metadata lookup; fall back to the request where needed.
        var origin = await _db.FulfillmentJobs
            .Where(j => j.MediaRequestId == request.Id && !j.IsUpgrade)
            .OrderByDescending(j => j.Id).FirstOrDefaultAsync();

        var episodesCsv = episodes.Count > 0
            ? string.Join(",", episodes.Select(e => $"S{e.season}E{e.episode}"))
            : null;

        _db.FulfillmentJobs.Add(new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            Title = request.Title,
            Year = origin?.Year,
            TmdbId = origin?.TmdbId ?? request.MediaId,
            ImdbId = origin?.ImdbId,
            TvdbId = origin?.TvdbId,
            ExternalId = request.ExternalId,
            ExternalSource = request.ExternalSource,
            RequestedEpisodesCsv = episodesCsv,
            Quality = target,
            GenresCsv = origin?.GenresCsv,
            IsAnime = origin?.IsAnime ?? false,
            IsUpgrade = true,
            ReplacePathsJson = replacePaths.Count > 0 ? JsonSerializer.Serialize(replacePaths) : null,
            Status = FulfillmentStatus.Queued,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<Quality> RecomputeAchievedQualityAsync(int mediaRequestId)
    {
        var req = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == mediaRequestId);
        if (req is null) return Quality.Any;

        // All video files currently imported for this request, across its jobs (superseded files are removed
        // when an upgrade replaces them, so what remains is the library's current state).
        var jobIds = await _db.FulfillmentJobs.Where(j => j.MediaRequestId == mediaRequestId)
            .Select(j => j.Id).ToListAsync();
        var heights = await _db.ImportedFiles
            .Where(f => jobIds.Contains(f.FulfillmentJobId) && f.FileType == "video" && f.ResolutionHeight > 0)
            .Select(f => f.ResolutionHeight)
            .ToListAsync();

        // Achieved quality is the WORST video we hold (a single 720p episode leaves a 1080p season below cutoff).
        var achieved = heights.Count == 0 ? Quality.Any : QualityHelper.FromHeight(heights.Min());
        req.AchievedQuality = achieved;

        // Target: re-resolve current admin rules so a later rule change re-flags cutoff correctly. Genres come
        // from the enqueue-time snapshot on the latest job (so genre-based override rules still apply). Only
        // mark cutoff unmet when we actually know the achieved quality (heights present).
        var genresCsv = await _db.FulfillmentJobs.Where(j => j.MediaRequestId == mediaRequestId)
            .OrderByDescending(j => j.Id).Select(j => j.GenresCsv).FirstOrDefaultAsync();
        var genres = string.IsNullOrWhiteSpace(genresCsv) ? null
            : genresCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var target = await _quality.ResolveQualityAsync(req.MediaType, req.MediaId, genres);
        req.CutoffMet = heights.Count == 0 || target == Quality.Any || (int)achieved >= (int)target;
        await _db.SaveChangesAsync();
        return achieved;
    }

    private Task<FulfillmentJobEntity?> LatestJobAsync(int mediaRequestId) =>
        _db.FulfillmentJobs
            .Where(x => x.MediaRequestId == mediaRequestId)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync();

    // What another in-flight job is already targeting for the same show, for cross-request dedup:
    // explicit episodes/seasons from its CSVs, or — when a job was enqueued pack-only because metadata
    // was unavailable at the time (both CSVs null) — "covers everything", since we can't enumerate what
    // it actually targets and shouldn't risk a duplicate whole-series download.
    private static (HashSet<int> seasons, HashSet<(int season, int episode)> episodes, bool coversEverything) ExpandJobScope(FulfillmentJobEntity job)
    {
        var seasons = new HashSet<int>();
        var episodes = new HashSet<(int, int)>();
        if (!string.IsNullOrWhiteSpace(job.RequestedEpisodesCsv))
        {
            foreach (var tok in job.RequestedEpisodesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var m = Regex.Match(tok, @"^[Ss](\d+)[Ee](\d+)$");
                if (!m.Success) continue;
                var s = int.Parse(m.Groups[1].Value);
                var e = int.Parse(m.Groups[2].Value);
                episodes.Add((s, e));
                seasons.Add(s);
            }
            return (seasons, episodes, false);
        }
        if (!string.IsNullOrWhiteSpace(job.RequestedSeasonsCsv))
        {
            foreach (var tok in job.RequestedSeasonsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(tok, out var s)) seasons.Add(s);
            return (seasons, episodes, false);
        }
        return (seasons, episodes, true);
    }

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
        SeasonTargets = string.IsNullOrWhiteSpace(j.SeasonTargetsJson)
            ? new List<SeasonTarget>()
            : (JsonSerializer.Deserialize<List<SeasonTarget>>(j.SeasonTargetsJson) ?? new List<SeasonTarget>()),
        Quality = j.Quality,
        Genres = string.IsNullOrWhiteSpace(j.GenresCsv)
            ? new List<string>()
            : j.GenresCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        IsAnime = j.IsAnime,
        Status = j.Status,
        Attempts = j.Attempts,
        Progress = j.Progress,
        IsUpgrade = j.IsUpgrade,
        ReplacePaths = string.IsNullOrWhiteSpace(j.ReplacePathsJson)
            ? new List<string>()
            : (JsonSerializer.Deserialize<List<string>>(j.ReplacePathsJson) ?? new List<string>())
    };
}
