using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// SQLite-backed <see cref="IDatabaseAdminService"/>. Deletions use ExecuteDelete in child-before-parent
/// order (FKs also cascade DB-side, so order is belt-and-braces); backups use VACUUM INTO, which produces
/// a consistent snapshot even while the app is live.
/// </summary>
public class DatabaseAdminService(AppDbContext db, IPlexApiService plex, ILogger<DatabaseAdminService> logger,
    UserGroupSeeder userGroups)
    : IDatabaseAdminService
{
    public async Task<DbOverviewDto> GetOverviewAsync()
    {
        var path = db.Database.GetDbConnection().DataSource ?? "app.db";
        long size = 0;
        try { if (File.Exists(path)) size = new FileInfo(path).Length; } catch { /* size is display-only */ }

        var tables = new List<DbTableCountDto>
        {
            new("Media requests", await db.MediaRequests.CountAsync()),
            new("Fulfillment jobs", await db.FulfillmentJobs.CountAsync()),
            new("Imported files (audit)", await db.ImportedFiles.CountAsync()),
            new("Watchlist items", await db.Watchlist.CountAsync()),
            new("Issues", await db.MediaIssues.CountAsync()),
            new("Users", await db.Users.CountAsync()),
            new("User profiles", await db.UserProfiles.CountAsync()),
            new("User access groups", await db.UserGroups.CountAsync()),
            new("User access audit", await db.UserAccessAudits.CountAsync()),
            new("Notifications", await db.Notifications.CountAsync()),
            new("Plex mappings (availability)", await db.PlexMappings.CountAsync()),
            new("Plex season availability", await db.PlexSeasonAvailability.CountAsync()),
            new("Metadata cache", await db.MediaMetadataCache.CountAsync()),
            new("Episode-list cache", await db.SeasonEpisodesCache.CountAsync()),
            new("Quality rules", await db.QualityRules.CountAsync()),
            new("Indexers", await db.Indexers.CountAsync()),
            new("Network shares", await db.NetworkShares.CountAsync()),
            new("Scheduled jobs", await db.ScheduledJobs.CountAsync()),
            new("Job run history", await db.JobRuns.CountAsync()),
            new("Discord outbox", await db.BridgeOutbox.CountAsync()),
        };
        return new DbOverviewDto(path, size, tables);
    }

    public async Task<string> CreateBackupAsync()
    {
        var source = db.Database.GetDbConnection().DataSource
                     ?? throw new InvalidOperationException("Could not resolve the database file path");
        var dir = Path.GetDirectoryName(Path.GetFullPath(source)) ?? ".";

        // One backup at a time on disk — these are meant to be downloaded, not archived server-side.
        foreach (var old in Directory.EnumerateFiles(dir, "app-backup-*.db"))
            try { File.Delete(old); } catch { /* best-effort */ }

        var target = Path.Combine(dir, $"app-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
        // VACUUM INTO writes a compacted, transactionally-consistent copy while the DB stays live.
        await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", target);
        logger.LogInformation("Database backup written to {Path}", target);
        return target;
    }

    public async Task CompactAsync() => await db.Database.ExecuteSqlRawAsync("VACUUM");

    public async Task<int> ClearMetadataCachesAsync()
    {
        var n = await db.MediaMetadataCache.ExecuteDeleteAsync()
              + await db.SeasonEpisodesCache.ExecuteDeleteAsync();
        logger.LogInformation("Admin cleared metadata caches ({Rows} rows)", n);
        return n;
    }

    public async Task<int> ClearAndRebuildAvailabilityIndexAsync()
    {
        var n = await db.PlexSeasonAvailability.ExecuteDeleteAsync()
              + await db.PlexMappings.ExecuteDeleteAsync();
        logger.LogInformation("Admin cleared the availability index ({Rows} rows); rebuilding from Plex", n);
        try { await plex.RebuildAvailabilityIndexAsync(); }
        catch (Exception ex) { logger.LogWarning(ex, "Availability index rebuild after clear failed; the background refresh will retry"); }
        return n;
    }

    public async Task<int> ClearJobHistoryAsync()
    {
        var n = await db.JobRuns.ExecuteDeleteAsync();
        logger.LogInformation("Admin cleared job run history ({Rows} rows)", n);
        return n;
    }

    public async Task<int> ClearNotificationsAsync()
    {
        var n = await db.Notifications.ExecuteDeleteAsync();
        logger.LogInformation("Admin cleared notifications ({Rows} rows)", n);
        return n;
    }

    public async Task<int> ClearClosedIssuesAsync()
    {
        var n = await db.MediaIssues.Where(i => i.Status != IssueStatus.Open).ExecuteDeleteAsync();
        logger.LogInformation("Admin cleared closed issues ({Rows} rows)", n);
        return n;
    }

    public async Task<int> ClearBridgeOutboxAsync()
    {
        var n = await db.BridgeOutbox.ExecuteDeleteAsync();
        logger.LogInformation("Admin cleared the Discord outbox ({Rows} rows)", n);
        return n;
    }

    public async Task<int> DeleteRequestsAsync(RequestStatus? status)
    {
        // Children first so counts are accurate even if DB-level cascades were ever disabled.
        var requestIds = await db.MediaRequests
            .Where(r => status == null || r.Status == status)
            .Select(r => r.Id).ToListAsync();
        if (requestIds.Count == 0) return 0;

        var jobIds = await db.FulfillmentJobs.Where(j => requestIds.Contains(j.MediaRequestId))
            .Select(j => j.Id).ToListAsync();
        await db.ImportedFiles.Where(f => jobIds.Contains(f.FulfillmentJobId)).ExecuteDeleteAsync();
        await db.FulfillmentJobs.Where(j => requestIds.Contains(j.MediaRequestId)).ExecuteDeleteAsync();
        await db.BridgeOutbox.Where(b => requestIds.Contains(b.MediaRequestId)).ExecuteDeleteAsync();
        var n = await db.MediaRequests.Where(r => requestIds.Contains(r.Id)).ExecuteDeleteAsync();

        logger.LogWarning("Admin deleted {Count} request(s) (status filter: {Status}) with their jobs/audit rows",
            n, status?.ToString() ?? "ALL");
        return n;
    }

    public async Task FactoryResetAsync(bool keepUsers)
    {
        logger.LogWarning("FACTORY RESET started (keepUsers={KeepUsers})", keepUsers);

        // Child tables before their parents; every table in the schema is listed.
        await db.ImportedFiles.ExecuteDeleteAsync();
        await db.FulfillmentJobs.ExecuteDeleteAsync();
        await db.BridgeOutbox.ExecuteDeleteAsync();
        await db.Notifications.ExecuteDeleteAsync();
        await db.MediaRequests.ExecuteDeleteAsync();
        await db.Watchlist.ExecuteDeleteAsync();
        await db.MediaIssues.ExecuteDeleteAsync();
        await db.PlexSeasonAvailability.ExecuteDeleteAsync();
        await db.PlexMappings.ExecuteDeleteAsync();
        await db.MediaMetadataCache.ExecuteDeleteAsync();
        await db.SeasonEpisodesCache.ExecuteDeleteAsync();
        await db.JobRuns.ExecuteDeleteAsync();
        await db.ScheduledJobs.ExecuteDeleteAsync();        // re-seeded by the scheduler on app restart
        await db.Indexers.ExecuteDeleteAsync();      // re-seeded on next panel view / worker poll
        await db.QualityRules.ExecuteDeleteAsync();
        await db.DownloadPreferences.ExecuteDeleteAsync();  // singleton re-created on demand
        await db.LibraryOrganizationPreferences.ExecuteDeleteAsync(); // singleton re-created on demand
        await db.NetworkShares.ExecuteDeleteAsync();
        if (!keepUsers)
        {
            await db.UserProfiles.ExecuteDeleteAsync();
            await db.UserGroups.ExecuteDeleteAsync();
            await db.Users.ExecuteDeleteAsync();
            await userGroups.SeedAsync();
        }

        await CompactAsync();
        logger.LogWarning("FACTORY RESET completed (keepUsers={KeepUsers})", keepUsers);
    }
}
