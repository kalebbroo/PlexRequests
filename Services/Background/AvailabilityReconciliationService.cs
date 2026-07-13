using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Background;

/// <summary>
/// Auto-marks open requests (Pending/Approved/Processing) as Available once their content shows up on
/// Plex — by ANY means, not just this app's downloader (manual add, an existing Sonarr/Radarr, or
/// content that was already in the library). Reconciles requests against the DB availability index
/// (kept fresh by <see cref="AvailabilityRefreshService"/>) and is season/episode aware. This is the
/// safety net that complements the downloader's /fulfilled callback.
/// </summary>
public class AvailabilityReconciliationService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<AvailabilityReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var plexConfigured = !string.IsNullOrWhiteSpace(config["Plex:PrimaryServerUrl"])
                             && !string.IsNullOrWhiteSpace(config["Plex:ServerToken"]);
        if (!config.GetValue("Reconciliation:Enabled", true) || !plexConfigured)
        {
            logger.LogInformation("Availability reconciliation idle (Reconciliation:Enabled + Plex config required)");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(5, config.GetValue("Reconciliation:IntervalMinutes", 30)));
        // Start a bit after the availability scan (startup +20s) so the index is populated first.
        try { await Task.Delay(TimeSpan.FromSeconds(70), stoppingToken); }
        catch (OperationCanceledException) { return; }

        logger.LogInformation("Availability reconciliation started (every {Interval}m)", interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Availability reconciliation pass failed"); }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One reconciliation pass. Returns how many requests were newly marked Available.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var plex = scope.ServiceProvider.GetRequiredService<IPlexApiService>();
        var notify = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var open = await db.MediaRequests
            .Where(r => r.Status == RequestStatus.Pending || r.Status == RequestStatus.Approved || r.Status == RequestStatus.Processing)
            .ToListAsync(ct);
        if (open.Count == 0) return 0;

        // Title-level availability for movies + whole-series TV, via the proven matcher (external ids).
        var cards = open.Select(r => new MediaCardDto { Id = r.MediaId, TmdbId = r.MediaId, MediaType = r.MediaType, Title = r.Title }).ToList();
        await plex.AnnotateAvailabilityAsync(cards);
        var titleAvailable = cards.ToDictionary(c => (c.MediaType, c.Id), c => c.IsAvailable);

        int marked = 0;
        foreach (var req in open)
        {
            if (ct.IsCancellationRequested) break;
            if (!await IsSatisfiedAsync(req, titleAvailable, db, plex, ct)) continue;

            req.Status = RequestStatus.Available;
            req.AvailableAt = DateTime.UtcNow;

            // If a fulfillment job was still open for it, close it — the content arrived another way.
            var job = await db.FulfillmentJobs
                .FirstOrDefaultAsync(j => j.MediaRequestId == req.Id
                    && j.Status != FulfillmentStatus.Completed && j.Status != FulfillmentStatus.Failed && j.Status != FulfillmentStatus.Cancelled, ct);
            if (job is not null) { job.Status = FulfillmentStatus.Completed; job.CompletedAt = DateTime.UtcNow; }

            await db.SaveChangesAsync(ct);
            await notify.RequestAvailableAsync(ToDto(req));
            marked++;
            logger.LogInformation("Reconciled request #{Id} \"{Title}\" -> Available (found on Plex)", req.Id, req.Title);
        }

        if (marked > 0) logger.LogInformation("Availability reconciliation marked {Count} request(s) Available", marked);
        return marked;
    }

    private static async Task<bool> IsSatisfiedAsync(MediaRequestEntity req, Dictionary<(MediaType, int), bool> titleAvailable,
        AppDbContext db, IPlexApiService plex, CancellationToken ct)
    {
        // Movies: available when the title is on Plex.
        if (req.MediaType != MediaType.TvShow)
            return titleAvailable.TryGetValue((req.MediaType, req.MediaId), out var mv) && mv;

        // TV, episode-level request: every requested episode must be present.
        if (!string.IsNullOrWhiteSpace(req.RequestedEpisodesCsv))
        {
            var wanted = ParseEpisodes(req.RequestedEpisodesCsv);
            if (wanted.Count == 0) return false;
            var have = await GetPlexEpisodesAsync(db, req.MediaId, ct);
            return wanted.All(w => have.TryGetValue(w.season, out var set) && set.Contains(w.episode));
        }

        // TV, season-level request: every requested season must be present.
        if (!string.IsNullOrWhiteSpace(req.RequestedSeasonsCsv))
        {
            var wanted = req.RequestedSeasonsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n : -1).Where(n => n >= 0).ToList();
            if (wanted.Count == 0) return false;
            var haveSeasons = (await plex.GetAvailableSeasonsAsync(req.MediaId)).ToHashSet();
            return wanted.All(haveSeasons.Contains);
        }

        // TV, whole series: available once the show is on Plex.
        return titleAvailable.TryGetValue((MediaType.TvShow, req.MediaId), out var tv) && tv;
    }

    // season -> set of available episode numbers, from the DB availability index (tmdb -> ratingKey -> seasons).
    private static async Task<Dictionary<int, HashSet<int>>> GetPlexEpisodesAsync(AppDbContext db, int tmdbId, CancellationToken ct)
    {
        var result = new Dictionary<int, HashSet<int>>();
        var ratingKey = await db.PlexMappings.Where(m => m.ExternalKey == $"tmdb:{tmdbId}").Select(m => m.RatingKey).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(ratingKey)) return result;
        var seasons = await db.PlexSeasonAvailability.Where(s => s.ShowRatingKey == ratingKey).ToListAsync(ct);
        foreach (var s in seasons)
        {
            var set = s.AvailableEpisodesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out var n) ? n : -1).Where(n => n >= 0).ToHashSet();
            result[s.SeasonNumber] = set;
        }
        return result;
    }

    private static List<(int season, int episode)> ParseEpisodes(string csv)
    {
        var list = new List<(int, int)>();
        foreach (var tok in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(tok, @"^[Ss](\d+)[Ee](\d+)$");
            if (m.Success) list.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
        }
        return list;
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
        RequestedByUserId = r.RequestedByUserId ?? 0,
        RequestedByUsername = r.RequestedBy ?? string.Empty
    };
}
