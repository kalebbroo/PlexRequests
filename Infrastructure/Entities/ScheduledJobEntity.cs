using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// One recurring background job managed by the generic scheduler. The scheduler ticks, finds enabled rows
/// whose <see cref="NextRunAt"/> has passed, dispatches to the matching <c>IJobHandler</c>, records a
/// <see cref="JobRunEntity"/>, and reschedules. There is exactly one row per <see cref="JobType"/>
/// (seeded on startup); the admin can enable/disable it, change its interval, or trigger it on-demand.
/// </summary>
public class ScheduledJobEntity
{
    public int Id { get; set; }

    /// <summary>Which handler runs. Unique — one schedule row per job type.</summary>
    public JobType JobType { get; set; }

    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>How often the job runs, in seconds. The admin edits this ("every 2h", "every 2 days").</summary>
    public int IntervalSeconds { get; set; } = 1800;

    /// <summary>When the scheduler should next dispatch this job. A manual "Run now" sets this to <c>UtcNow</c>.</summary>
    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }

    public JobRunStatus? LastStatus { get; set; }
    [MaxLength(1000)]
    public string? LastMessage { get; set; }

    /// <summary>Guard flag so a long run isn't double-dispatched by an overlapping tick. Cleared when the run ends.</summary>
    public bool IsRunning { get; set; }

    /// <summary>Set by an admin "Run now"; the next dispatch records the run as manually triggered and clears this.</summary>
    public bool ManualRunRequested { get; set; }

    /// <summary>Optional handler-specific configuration (JSON). Unused by the built-in jobs today; here so a
    /// new job type can carry parameters without a schema change.</summary>
    public string? PayloadJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
