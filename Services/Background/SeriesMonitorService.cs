using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Background;

/// <summary>
/// Ongoing-series auto-download. For each whole-series request marked <c>Monitored</c> that has already
/// been fulfilled (Status = Available — i.e. its back-catalog is in), this checks TMDB for episodes that
/// have aired but aren't on Plex yet and spawns an auto-approved child request for each, which flows
/// through the normal fulfillment pipeline. Runs a few times a day. Dedup ensures each episode is queued
/// once; anything already on Plex (per the DB availability index) is skipped.
/// </summary>
public class SeriesMonitorService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<SeriesMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Monitoring:Enabled", true) || !config.GetValue<bool>("Fulfillment:Enabled"))
        {
            logger.LogInformation("Series monitoring idle (Monitoring:Enabled + Fulfillment:Enabled required)");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(15, config.GetValue("Monitoring:IntervalMinutes", 360)));
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        logger.LogInformation("Series monitor started (every {Interval}m)", interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Series monitor pass failed"); }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One monitor pass. Returns how many new episode requests were queued.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var plex = scope.ServiceProvider.GetRequiredService<IPlexApiService>();
        var metadata = scope.ServiceProvider.GetRequiredService<IMediaMetadataProvider>();
        var requests = scope.ServiceProvider.GetRequiredService<IMediaRequestService>();

        var anchors = await db.MediaRequests.AsNoTracking()
            .Where(r => r.Monitored && r.MediaType == MediaType.TvShow && r.Status == RequestStatus.Available)
            .ToListAsync(ct);
        if (anchors.Count == 0) return 0;

        int queued = 0;
        foreach (var anchor in anchors)
        {
            if (ct.IsCancellationRequested) break;

            // Episodes already covered by an active (non-terminal) request for this show — don't re-queue.
            var activeCsvs = await db.MediaRequests.AsNoTracking()
                .Where(r => r.MediaId == anchor.MediaId && r.MediaType == MediaType.TvShow
                            && r.RequestedEpisodesCsv != null
                            && r.Status != RequestStatus.Cancelled && r.Status != RequestStatus.Rejected && r.Status != RequestStatus.Failed)
                .Select(r => r.RequestedEpisodesCsv!)
                .ToListAsync(ct);
            var covered = new HashSet<string>(
                activeCsvs.SelectMany(c => c.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                StringComparer.OrdinalIgnoreCase);

            var details = await metadata.GetDetailsAsync(anchor.MediaId, MediaType.TvShow);
            var seasonNumbers = details?.Seasons?.Where(s => s.SeasonNumber > 0).Select(s => s.SeasonNumber).Distinct() ?? Enumerable.Empty<int>();

            foreach (var seasonNum in seasonNumbers)
            {
                if (ct.IsCancellationRequested) break;
                var eps = await plex.GetSeasonEpisodesAsync(anchor.MediaId, seasonNum); // IsAvailable + HasAired overlaid

                // Batch this season's newly-aired missing episodes into ONE request. A single request means
                // a single downloader job that can grab a season pack (and trim it to just these episodes)
                // when no standalone per-episode releases exist — instead of one doomed job per episode.
                var missing = new List<(int season, int episode)>();
                foreach (var ep in eps.Where(e => e.HasAired && !e.IsAvailable))
                {
                    var tag = $"S{ep.SeasonNumber}E{ep.EpisodeNumber}";
                    if (!covered.Add(tag)) continue; // already covered/queued this pass
                    missing.Add((ep.SeasonNumber, ep.EpisodeNumber));
                }
                if (missing.Count == 0) continue;

                var result = await requests.CreateMonitoredEpisodesAsync(anchor.Id, missing);
                if (result.Success)
                {
                    queued += missing.Count;
                    logger.LogInformation("Monitor queued {Count} episode(s) of \"{Title}\" S{Season} (anchor #{Anchor})",
                        missing.Count, anchor.Title, seasonNum, anchor.Id);
                }
            }
        }

        if (queued > 0) logger.LogInformation("Series monitor queued {Count} new episode(s)", queued);
        return queued;
    }
}
