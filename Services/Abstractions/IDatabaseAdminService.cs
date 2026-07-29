using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Abstractions;

public record DbTableCountDto(string Table, int Rows);
public record DbOverviewDto(string Path, long SizeBytes, List<DbTableCountDto> Tables);

/// <summary>
/// Backs the admin "System → Database" panel: visibility into the SQLite file (path/size/row counts),
/// on-demand backups, targeted cleanup for when something breaks, and the full factory reset. Every
/// destructive method returns the number of rows removed so the UI can report what actually happened.
/// </summary>
public interface IDatabaseAdminService
{
    Task<DbOverviewDto> GetOverviewAsync();

    /// <summary>Write a consistent point-in-time copy of the database (SQLite VACUUM INTO) and return its
    /// path. Any previous backup files are removed first so they don't accumulate.</summary>
    Task<string> CreateBackupAsync();

    /// <summary>Reclaim free pages after large deletes (SQLite VACUUM).</summary>
    Task CompactAsync();

    // ---- Targeted cleanup: safe, rebuildable state ---------------------------------------------------
    /// <summary>TMDB metadata + episode-list caches. Re-fetched on demand; no user data touched.</summary>
    Task<int> ClearMetadataCachesAsync();
    /// <summary>Plex availability index (mappings + per-season availability). Cleared then rebuilt live.</summary>
    Task<int> ClearAndRebuildAvailabilityIndexAsync();
    /// <summary>Scheduled-job run history (the Jobs → Activity feed).</summary>
    Task<int> ClearJobHistoryAsync();
    /// <summary>All in-app notifications, read or not.</summary>
    Task<int> ClearNotificationsAsync();
    /// <summary>Resolved/dismissed issue reports (open ones are kept).</summary>
    Task<int> ClearClosedIssuesAsync();
    /// <summary>Delivered/pending Discord bridge outbox rows.</summary>
    Task<int> ClearBridgeOutboxAsync();

    // ---- Requests ------------------------------------------------------------------------------------
    /// <summary>Delete requests (null status = ALL requests). Their fulfillment jobs and import-audit rows
    /// cascade away with them; library files on disk are never touched.</summary>
    Task<int> DeleteRequestsAsync(RequestStatus? status);

    // ---- Danger zone ---------------------------------------------------------------------------------
    /// <summary>Empty every table (optionally keeping user accounts) and compact the file — the "start
    /// over fresh" button. Settings singletons, the job schedule, and indexer rows re-seed themselves on
    /// demand/restart; admins re-gain the role from ADMIN_USERNAMES at next sign-in.</summary>
    Task FactoryResetAsync(bool keepUsers);
}
