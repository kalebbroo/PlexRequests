using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Jobs;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using System.Text.Json;

namespace PlexRequestsHosted.Services.Background;

/// <summary>
/// Auto-marks open requests (Pending/Approved/Processing) as Available once their content shows up on
/// Plex — by ANY means, not just this app's downloader (manual add, an existing Sonarr/Radarr, or
/// content that was already in the library). Reconciles requests against the DB availability index
/// (kept fresh by <see cref="AvailabilityRefreshService"/>) and is season/episode aware. This is the
/// safety net that complements the downloader's /fulfilled callback.
/// </summary>
public class AvailabilityReconciliationService(
    AppDbContext db,
    IPlexApiService plex,
    ISeasonAvailabilityEvaluator seasonEvaluator,
    PlexRequestsHosted.Services.Implementations.IPlexMusicService music,
    INotificationService notify,
    IConfiguration config,
    ILogger<AvailabilityReconciliationService> logger) : IJobHandler
{
    // Every status a request can sit in while its content might still show up by some other route. Searching
    // and PartiallyAvailable belong here and were missing: the reaper and the downloader's defer callback both
    // park requests in Searching, nothing moved them back out, and this query skipped them — so the requests
    // most likely to be satisfied another way were precisely the ones the safety net ignored.
    private static readonly RequestStatus[] OpenStatuses =
    {
        RequestStatus.Pending, RequestStatus.Approved, RequestStatus.Processing,
        RequestStatus.Searching, RequestStatus.PartiallyAvailable
    };

    public JobType Type => JobType.AvailabilityReconcile;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var plexConfigured = !string.IsNullOrWhiteSpace(config["Plex:PrimaryServerUrl"])
                             && !string.IsNullOrWhiteSpace(config["Plex:ServerToken"]);
        if (!config.GetValue("Reconciliation:Enabled", true)) return JobResult.Skipped("Reconciliation is disabled");
        if (!plexConfigured) return JobResult.Skipped("Plex is not configured");

        var marked = await RunOnceAsync(ct);
        return marked > 0
            ? JobResult.Ok(marked, $"Marked {marked} request(s) Available")
            : JobResult.Skipped("No open request was satisfied by the Plex library");
    }

    /// <summary>One reconciliation pass. Returns how many requests were newly marked Available.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var open = await db.MediaRequests
            .Where(r => OpenStatuses.Contains(r.Status))
            .ToListAsync(ct);
        if (open.Count == 0) return 0;

        // Title-level availability for movies + whole-series TV, via the proven matcher (external ids).
        // Deduped by (MediaType, MediaId): a show routinely has many open MediaRequest rows at once — one per
        // monitored episode, plus separate season/whole-series requests — and availability doesn't depend on
        // which of those rows asked, so building one card per request crashed the dictionary below the moment
        // any title had more than one open request.
        var cards = open.Where(r => r.MediaType != MediaType.Music)
            .Select(r => new MediaCardDto { Id = r.MediaId, TmdbId = r.MediaId, MediaType = r.MediaType, Title = r.Title })
            .DistinctBy(c => (c.MediaType, c.Id)).ToList();
        await plex.AnnotateAvailabilityAsync(cards);
        var titleAvailable = cards.ToDictionary(c => (c.MediaType, c.Id), c => c.IsAvailable);

        int marked = 0;
        foreach (var req in open)
        {
            if (ct.IsCancellationRequested) break;
            var satisfied = req.MediaType == MediaType.Music
                ? await IsMusicSatisfiedAsync(req, ct)
                : await IsSatisfiedAsync(req, titleAvailable, plex, seasonEvaluator, ct);
            if (!satisfied) continue;

            req.Status = RequestStatus.Available;
            req.AvailableAt = DateTime.UtcNow;

            // If a fulfillment job was still open for it, close it — the content arrived another way.
            var job = await db.FulfillmentJobs
                .FirstOrDefaultAsync(j => j.MediaRequestId == req.Id
                    && (j.Status == FulfillmentStatus.Queued || j.Status == FulfillmentStatus.Claimed
                        || j.Status == FulfillmentStatus.Downloading || j.Status == FulfillmentStatus.Deferred), ct);
            if (job is not null) { job.Status = FulfillmentStatus.Completed; job.CompletedAt = DateTime.UtcNow; }

            await db.SaveChangesAsync(ct);
            await notify.RequestAvailableAsync(ToDto(req));
            marked++;
            logger.LogInformation("Reconciled request #{Id} \"{Title}\" -> Available (found on Plex)", req.Id, req.Title);
        }

        if (marked > 0) logger.LogInformation("Availability reconciliation marked {Count} request(s) Available", marked);
        return marked;
    }

    private async Task<bool> IsMusicSatisfiedAsync(MediaRequestEntity req, CancellationToken ct)
    {
        var contextJson = await db.FulfillmentJobs.AsNoTracking()
            .Where(j => j.MediaRequestId == req.Id && j.AcquisitionContextJson != null)
            .OrderByDescending(j => j.Id)
            .Select(j => j.AcquisitionContextJson)
            .FirstOrDefaultAsync(ct);
        MusicAcquisitionContextDto? context = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(contextJson))
                context = JsonSerializer.Deserialize<MusicAcquisitionContextDto>(contextJson);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Music verification context for request {RequestId} is malformed", req.Id);
        }
        context ??= new MusicAcquisitionContextDto
        {
            Kind = req.RequestScopeKind switch
            {
                RequestScopeKind.ArtistCatalog => MediaKind.Artist,
                RequestScopeKind.Track => MediaKind.Track,
                _ => MediaKind.Album
            },
            Artist = req.RequestScopeKind == RequestScopeKind.ArtistCatalog ? req.Title : null,
            Album = req.RequestScopeKind == RequestScopeKind.Album ? req.Title : null,
            Track = req.RequestScopeKind == RequestScopeKind.Track ? req.Title : null
        };
        var result = await music.VerifyAsync(new PlexVerificationRequest(
            MediaType.Music, context.Kind, req.ExternalSource, req.ExternalId,
            context.Artist, context.Album, context.Track, context.ExpectedAlbums,
            context.Tracks.Select(x => x.Title).ToList(),
            context.Tracks.FirstOrDefault()?.DurationMs), ct);
        return result.Available;
    }

    private static async Task<bool> IsSatisfiedAsync(MediaRequestEntity req, Dictionary<(MediaType, int), bool> titleAvailable,
        IPlexApiService plex, ISeasonAvailabilityEvaluator seasonEvaluator, CancellationToken ct)
    {
        // Movies: available when the title is on Plex.
        if (req.MediaType != MediaType.TvShow)
            return titleAvailable.TryGetValue((req.MediaType, req.MediaId), out var mv) && mv;

        // TV, episode-level request: every requested episode must be present.
        if (!string.IsNullOrWhiteSpace(req.RequestedEpisodesCsv))
        {
            var wanted = ParseEpisodes(req.RequestedEpisodesCsv);
            if (wanted.Count == 0) return false;
            var have = await seasonEvaluator.GetPlexEpisodesAsync(req.MediaId, ct);
            return wanted.All(w => have.TryGetValue(w.season, out var set) && set.Contains(w.episode));
        }

        // TV, season-level request: every requested season must be FULLY complete (all of its episodes,
        // not just one) — GetAvailableSeasonsAsync now delegates to the shared evaluator for this.
        if (!string.IsNullOrWhiteSpace(req.RequestedSeasonsCsv))
        {
            var wanted = req.RequestedSeasonsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n : -1).Where(n => n >= 0).ToList();
            if (wanted.Count == 0) return false;
            var haveSeasons = (await plex.GetAvailableSeasonsAsync(req.MediaId)).ToHashSet();
            return wanted.All(haveSeasons.Contains);
        }

        // TV, whole series: every season that has aired must be fully complete on Plex — not merely
        // "the show exists in the library at all", which used to fire the instant even one episode landed.
        return await seasonEvaluator.IsWholeSeriesSatisfiedAsync(req.MediaId, ct);
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
