using PlexRequestsHosted.Services.Abstractions;

namespace PlexRequestsHosted.Services.Background;

/// <summary>
/// Keeps the DB-backed Plex availability index fresh. Runs a full Plex scan shortly after startup and
/// then on an interval (default 30 min), upserting item id-maps + per-season episode presence and
/// pruning anything no longer on the server. All availability reads (badges, per-season dedup) come
/// from the DB this fills, so nothing user-facing has to touch Plex live.
/// </summary>
public class AvailabilityRefreshService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<AvailabilityRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var plexConfigured = !string.IsNullOrWhiteSpace(config["Plex:PrimaryServerUrl"])
                             && !string.IsNullOrWhiteSpace(config["Plex:ServerToken"]);
        if (!plexConfigured)
        {
            logger.LogInformation("Plex not configured; availability refresh service idle");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(5, config.GetValue("Plex:AvailabilityRefreshMinutes", 30)));
        // Small startup delay so the app finishes booting before the first (heavy) scan.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        logger.LogInformation("Availability refresh started (every {Interval}m)", interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Availability refresh pass failed"); }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunOnceAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var plex = scope.ServiceProvider.GetRequiredService<IPlexApiService>();
        var result = await plex.RebuildAvailabilityFromPlexAsync();
        logger.LogInformation("Availability refresh pass complete: {@Result}", result);
    }
}
