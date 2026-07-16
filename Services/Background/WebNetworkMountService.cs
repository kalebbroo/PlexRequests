using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared;

namespace PlexRequestsHosted.Services.Background;

/// <summary>Lets the admin UI ask the mount service to reconcile immediately (after a save/test)
/// instead of waiting for the periodic tick.</summary>
public interface INetworkMountController
{
    /// <summary>Trigger a reconcile now and return once it has completed (so the UI can show fresh status).</summary>
    Task ReconcileNowAsync(CancellationToken ct = default);
}

/// <summary>
/// Keeps the web container's read-only mounts under /mnt/nas in sync with the configured shares, so the
/// folder browser shows the real NAS tree. Read-only here — the web app never writes to a share; the
/// downloader mounts the same shares read-write for placing files. Runs a reconcile on startup, every
/// <see cref="RefreshInterval"/>, and on demand (after an admin save/test). Doubles as the connection
/// test: a failed mount surfaces its error into <see cref="INetworkMountStatusStore"/> for the UI.
/// </summary>
public class WebNetworkMountService(
    IServiceScopeFactory scopeFactory,
    INetworkMountStatusStore status,
    IConfiguration config,
    ILogger<WebNetworkMountService> logger) : BackgroundService, INetworkMountController
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _gate = new(1, 1); // serialize reconciles (startup vs on-demand)

    public async Task ReconcileNowAsync(CancellationToken ct = default)
    {
        await ReconcileAsync(ct);
        _signal.Release(); // also wake the loop so its next wait restarts cleanly
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var (uid, gid) = ReadIds();
        logger.LogInformation("Network-share mount service started (read-only, uid={Uid} gid={Gid})", uid, gid);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Network-share reconcile failed"); }

            try { await _signal.WaitAsync(RefreshInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var shares = scope.ServiceProvider.GetRequiredService<INetworkShareService>();
            var configs = await shares.GetMountConfigsAsync();

            var (uid, gid) = ReadIds();
            var results = await NetworkMountHelper.ReconcileAsync(
                configs, readOnly: true, uid, gid, m => logger.LogInformation("{Msg}", m), ct);

            foreach (var r in results) status.Set(r.MountSlug, r.Mounted, r.Error);
            status.Prune(configs.Select(c => c.MountSlug));
        }
        finally { _gate.Release(); }
    }

    private (int uid, int gid) ReadIds() =>
        (config.GetValue("NetworkShares:Uid", 1000), config.GetValue("NetworkShares:Gid", 1000));
}
