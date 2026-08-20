using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlexRequestsHosted.Infrastructure.Catalog;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequestsHosted.Services.Background;

/// <summary>
/// Keeps the derived catalog bounded. Rows referenced by download history or the release blocklist are
/// retained; everything else ages out in small transactions so maintenance cannot hold SQLite's writer
/// lock for an unbounded delete.
/// </summary>
public sealed class CatalogMaintenanceWorker(
    IDbContextFactory<CatalogDbContext> catalogFactory,
    IDbContextFactory<AppDbContext> appFactory,
    IOptions<CatalogOptions> options,
    ILogger<CatalogMaintenanceWorker> logger) : BackgroundService
{
    private const int DeleteBatchSize = 1000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PruneAsync(DateTime.UtcNow, stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Release catalog maintenance failed"); }

            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task PruneAsync(DateTime now, CancellationToken cancellationToken)
    {
        var catalogCutoff = now - TimeSpan.FromDays(Math.Clamp(options.Value.RetentionDays, 7, 3650));
        var unresolvedCutoff = now - TimeSpan.FromDays(Math.Clamp(options.Value.UnresolvedRetentionDays, 1, 365));
        var receiptCutoff = now - TimeSpan.FromDays(7);

        await using var app = await appFactory.CreateDbContextAsync(cancellationToken);
        var torrentHashes = await app.FulfillmentTransfers.AsNoTracking()
            .Where(x => x.Protocol == PlexRequestsHosted.Shared.Enums.AcquisitionProtocol.Torrent && x.SourceId != null)
            .Select(x => x.SourceId!)
            .ToListAsync(cancellationToken);
        var blockedHashes = await app.ReleaseBlocklist.AsNoTracking()
            .Where(x => x.InfoHash != null)
            .Select(x => x.InfoHash!)
            .ToListAsync(cancellationToken);
        var protectedHashes = torrentHashes.Concat(blockedHashes)
            .Select(MagnetUtil.Normalize)
            .Where(x => x is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unresolvedDeleted = await DeleteInBatchesAsync(
            db => db.Sightings.Where(x => x.ReleaseId == null && x.LastSeenAt < unresolvedCutoff),
            catalogFactory,
            cancellationToken);
        var receiptsDeleted = await DeleteInBatchesAsync(
            db => db.BatchReceipts.Where(x => x.CommittedAt < receiptCutoff),
            catalogFactory,
            cancellationToken);

        var releasesDeleted = 0;
        while (true)
        {
            await using var catalog = await catalogFactory.CreateDbContextAsync(cancellationToken);
            var ids = await catalog.Releases.AsNoTracking()
                .Where(x => x.LastSeenAt < catalogCutoff && !protectedHashes.Contains(x.InfoHash))
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .Take(DeleteBatchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0) break;
            releasesDeleted += await catalog.Releases.Where(x => ids.Contains(x.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        await using var stats = await catalogFactory.CreateDbContextAsync(cancellationToken);
        var dataSource = stats.Database.GetDbConnection().DataSource;
        var size = File.Exists(dataSource) ? new FileInfo(dataSource).Length : 0;
        var maxBytes = Math.Max(128, options.Value.MaxDatabaseSizeMb) * 1024L * 1024L;
        if (size > maxBytes)
            logger.LogWarning("Catalog database is {SizeMb:F1} MB, above its configured {MaxMb} MB limit; retention removed only eligible rows",
                size / 1024d / 1024d, options.Value.MaxDatabaseSizeMb);

        if (unresolvedDeleted + receiptsDeleted + releasesDeleted > 0)
            logger.LogInformation("Catalog maintenance removed {Releases} release(s), {Unresolved} unresolved sighting(s), and {Receipts} receipt(s)",
                releasesDeleted, unresolvedDeleted, receiptsDeleted);
    }

    private static async Task<int> DeleteInBatchesAsync<TEntity>(
        Func<CatalogDbContext, IQueryable<TEntity>> query,
        IDbContextFactory<CatalogDbContext> factory,
        CancellationToken cancellationToken) where TEntity : class
    {
        var total = 0;
        while (true)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var rows = await query(db).Take(DeleteBatchSize).ExecuteDeleteAsync(cancellationToken);
            total += rows;
            if (rows < DeleteBatchSize) return total;
        }
    }
}
