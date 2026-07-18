using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// One execution record for a scheduled job — the activity/history feed shown in the admin Jobs UI. Written
/// when a run starts (Running) and updated on completion with its outcome, a short message, and how many
/// items it processed (e.g. how many deferred requests it re-queued, how many upgrades it enqueued).
/// </summary>
public class JobRunEntity
{
    public int Id { get; set; }

    public JobType JobType { get; set; }

    /// <summary>The schedule row this run came from (null for a run not tied to a schedule).</summary>
    public int? ScheduledJobId { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }

    public JobRunStatus Status { get; set; } = JobRunStatus.Running;

    /// <summary>How many units of work this run acted on (jobs re-queued, upgrades enqueued, ...).</summary>
    public int ItemsProcessed { get; set; }

    [MaxLength(2000)]
    public string? Message { get; set; }

    /// <summary>True when an admin pressed "Run now" rather than the scheduler firing on its interval.</summary>
    public bool TriggeredManually { get; set; }
}
