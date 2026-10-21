using System.Diagnostics;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Discovery;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Background;

/// <summary>
/// Keeps what the home and browse pages read already cached, so a visitor does not pay for it. It warms the TMDB
/// discovery lists those pages request, and it rebuilds the Plex availability index on a short timer. A missed
/// pass only means the next visitor pays the cost they would have paid anyway, so failures are logged and the loop
/// carries on.
/// </summary>
public sealed class CacheWarmupService(
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<CacheWarmupService> logger) : BackgroundService
{
    // Let migrations and the first requests finish before any outbound call.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);
    // Shorter than the availability index's TTL, so a missed tick never leaves the index cold.
    private static readonly TimeSpan AvailabilityInterval = TimeSpan.FromMinutes(4);
    // Shorter than the one-hour TMDB discovery cache, so the lists are refreshed before they expire.
    private static readonly TimeSpan DiscoverInterval = TimeSpan.FromMinutes(45);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        var nextDiscover = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            await WarmAvailabilityAsync();
            if (DateTime.UtcNow >= nextDiscover)
            {
                await WarmDiscoveryAsync();
                nextDiscover = DateTime.UtcNow + DiscoverInterval;
            }

            try { await Task.Delay(AvailabilityInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task WarmAvailabilityAsync()
    {
        if (string.IsNullOrWhiteSpace(config["Plex:PrimaryServerUrl"]) || string.IsNullOrWhiteSpace(config["Plex:ServerToken"]))
            return;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IPlexApiService>().WarmAvailabilityIndexAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Availability index warm-up failed");
        }
    }

    private async Task WarmDiscoveryAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        var plex = scope.ServiceProvider.GetRequiredService<IPlexApiService>();
        var size = DiscoveryRows.RowSize;

        // Exactly the calls the home and browse pages make. Run one at a time so the warm-up never bursts TMDB.
        var calls = new List<Func<Task>>
        {
            () => plex.GetTrendingAsync(null, 1, size),
            () => plex.GetPopularAsync(MediaType.Movie, 1, size),
            () => plex.GetPopularAsync(MediaType.TvShow, 1, size),
            () => plex.GetTopRatedAsync(MediaType.Movie, 1, size),
            () => plex.GetTopRatedAsync(MediaType.TvShow, 1, size),
            () => plex.GetByGenreAsync(MediaType.Movie, "Action", 1, size),
        };
        foreach (var type in new[] { MediaType.Movie, MediaType.TvShow })
        {
            var mediaType = type;
            calls.Add(() => plex.GetTrendingAsync(mediaType, 1, size));
            calls.Add(() => plex.GetPopularAsync(mediaType, 1, size));
            calls.Add(() => plex.GetTopRatedAsync(mediaType, 1, size));
            foreach (var genre in DiscoveryRows.RowGenres(mediaType))
                calls.Add(() => plex.GetByGenreAsync(mediaType, genre, 1, size));
        }

        var failed = 0;
        var clock = Stopwatch.StartNew();
        foreach (var call in calls)
        {
            try { await call(); }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex, "Discovery warm-up call failed");
            }
        }
        logger.LogInformation("Discovery warm-up finished: {Lists} lists, {Failed} failed, {Ms} ms",
            calls.Count, failed, clock.ElapsedMilliseconds);
    }
}
