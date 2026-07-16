using Microsoft.Extensions.DependencyInjection;
using PlexRequests.Downloader.Api;
using PlexRequestsHosted.Shared;

namespace PlexRequests.Downloader.Worker;

/// <summary>
/// Keeps the downloader container's read-write mounts under /mnt/nas in sync with the admin-configured
/// network shares, so the organizer can place files onto a NAS. Fetches the shares (with credentials)
/// from the web app's secured fulfillment API and reconciles the OS mount table on startup and every
/// refresh interval. Mounts read-write here (the downloader is the writer); the web app mounts the same
/// shares read-only for the folder browser. A share whose mount fails is simply skipped and retried —
/// downloads to local paths keep working regardless.
/// </summary>
public class NetworkMountService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<NetworkMountService> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var (uid, gid) = (config.GetValue("NetworkShares:Uid", 1000), config.GetValue("NetworkShares:Gid", 1000));
        logger.LogInformation("Network-share mount service started (read-write, uid={Uid} gid={Gid})", uid, gid);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var api = scope.ServiceProvider.GetRequiredService<IPlexRequestsApiClient>();
                var configs = await api.GetNetworkSharesAsync(stoppingToken);
                if (configs is not null) // null = web unreachable; leave existing mounts untouched
                {
                    var results = await NetworkMountHelper.ReconcileAsync(
                        configs, readOnly: false, uid, gid, m => logger.LogInformation("{Msg}", m), stoppingToken);
                    foreach (var r in results.Where(r => !r.Mounted && r.Error is not null))
                        logger.LogWarning("Network share '{Slug}' not mounted: {Error}", r.MountSlug, r.Error);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Network-share reconcile failed"); }

            try { await Task.Delay(RefreshInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
