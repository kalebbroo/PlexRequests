using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Api;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Worker;

/// <summary>
/// Watches indexers' newest releases for anything currently wanted, and grabs it the moment it appears.
///
/// This is the safety net that makes the air-date calendar's imprecision acceptable. The calendar can only
/// estimate when an episode airs — TMDB reports a date and no time — so on its own it would sometimes look
/// too early or too late. Sweeping every quarter of an hour means the worst case is a short wait rather
/// than a missed episode.
///
/// It lives in the downloader because it needs indexer access, which belongs behind the VPN tunnel.
/// </summary>
public class RssSweepWorker(
    IPlexRequestsApiClient api,
    IIndexerClient indexer,
    IIndexerSettingsProvider indexerSettings,
    IReleaseParser parser,
    IOptions<CatalogWorkerOptions> catalogOptions,
    ILogger<RssSweepWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        logger.LogInformation("Automatic release monitoring started (every {Minutes} minutes)", Interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Automatic release monitoring failed"); }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var wanted = await api.GetWantedAsync(ct);
        if (wanted.Count == 0) return;

        await indexerSettings.RefreshAsync(ct);
        var sources = indexerSettings.All.Where(i => i.Enabled && i.EnableIngestion && i.SupportsTv).ToList();
        if (sources.Count == 0) return;

        // Index what's wanted by (show, season, episode) so matching a release is a dictionary hit rather
        // than a scan per candidate.
        var index = wanted
            .GroupBy(w => (w.Title.ToLowerInvariant(), w.Season, w.Episode))
            .ToDictionary(g => g.Key, g => g.First());

        var grabs = new List<RssGrabDto>();

        foreach (var w in wanted.GroupBy(x => x.MediaRequestId).Select(g => g.First()))
        {
            if (ct.IsCancellationRequested) break;

            // Reuse the ordinary search path: an episode-scoped job for this show. The sweep is about
            // latency, not a different matching rule, so the matching stays identical to everything else.
            var job = new FulfillmentJobDto
            {
                MediaRequestId = w.MediaRequestId,
                MediaId = w.ShowTmdbId,
                TmdbId = w.ShowTmdbId,
                MediaType = w.IsAnime ? MediaType.Anime : MediaType.TvShow,
                Title = w.Title,
                ImdbId = w.ImdbId,
                IsAnime = w.IsAnime,
                Quality = Quality.Any
            };

            IReadOnlyList<ReleaseCandidate> candidates;
            if (catalogOptions.Value.Enabled && catalogOptions.Value.UseForMonitoring)
            {
                // Catalog-only monitoring is the opt-in replacement for N wanted shows × M live sources.
                // If the local API itself is unavailable, retain the established live path for this pass.
                candidates = await api.SearchCatalogAsync(job, ct)
                    ?? (await indexer.SearchAsync(job, ct, IndexerSearchPurpose.Monitoring)).Candidates;
            }
            else
            {
                candidates = (await indexer.SearchAsync(job, ct, IndexerSearchPurpose.Monitoring)).Candidates;
            }

            foreach (var c in candidates)
            {
                var parsed = parser.Parse(c.ReleaseName);
                if (parsed.Season is not int s || parsed.Episode is not int e) continue;
                if (!index.TryGetValue((w.Title.ToLowerInvariant(), s, e), out var hit)) continue;

                grabs.Add(new RssGrabDto
                {
                    MediaRequestId = hit.MediaRequestId,
                    Season = s,
                    Episode = e,
                    ReleaseName = c.ReleaseName,
                    Magnet = c.Magnet,
                    InfoHash = MagnetUtil.Normalize(c.InfoHash) ?? MagnetUtil.InfoHashFromMagnet(c.Magnet),
                    IndexerId = c.IndexerId
                });
            }
        }

        if (grabs.Count == 0) return;

        // One grab per episode — several indexers will each carry the same release.
        var deduped = grabs
            .GroupBy(g => (g.MediaRequestId, g.Season, g.Episode))
            .Select(g => g.First())
            .ToList();

        var accepted = await api.ReportRssGrabsAsync(deduped, ct);
        logger.LogInformation("Release monitoring matched {Matched} wanted episode(s), {Accepted} queued", deduped.Count, accepted);
    }
}
