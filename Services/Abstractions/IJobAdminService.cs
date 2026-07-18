using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Abstractions;

/// <summary>
/// Read/write surface behind the admin "Jobs" panel: the recurring-job schedule, the run-history feed, the
/// "Missing" list (requests still being searched for), and the "Cutoff Unmet" list (available titles below
/// their preferred quality). All actions are admin-only (the panel is gated by <c>[Authorize(Roles="Admin")]</c>).
/// </summary>
public interface IJobAdminService
{
    // --- Scheduled jobs ---
    Task<List<ScheduledJobDto>> GetScheduledJobsAsync();
    /// <summary>Enable or disable a recurring job.</summary>
    Task<bool> SetJobEnabledAsync(JobType type, bool enabled);
    /// <summary>Change how often a recurring job runs (seconds; clamped to a sane minimum).</summary>
    Task<bool> SetJobIntervalAsync(JobType type, int intervalSeconds);
    /// <summary>Trigger a recurring job to run as soon as the scheduler next ticks (sets its NextRunAt to now).</summary>
    Task<bool> RunJobNowAsync(JobType type);

    // --- History ---
    Task<List<JobRunDto>> GetRecentRunsAsync(int take = 50);

    // --- Missing (still-searching) list ---
    Task<List<WantedItemDto>> GetMissingAsync();
    /// <summary>Re-search a single parked request immediately (flip it back to Queued for the downloader).</summary>
    Task<bool> SearchMissingNowAsync(int jobId);
    /// <summary>Stop searching for a parked request and mark it failed (admin gives up on it).</summary>
    Task<bool> CancelMissingAsync(int jobId);

    // --- Cutoff Unmet (upgrade candidates) list ---
    Task<List<CutoffUnmetItemDto>> GetCutoffUnmetAsync();
    /// <summary>Enqueue an immediate quality-upgrade search for one available-but-below-target request.</summary>
    Task<bool> UpgradeNowAsync(int requestId);
}
