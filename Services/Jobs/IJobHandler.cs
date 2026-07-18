using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Jobs;

/// <summary>
/// One kind of work the generic <c>JobSchedulerService</c> can run. Each handler declares the
/// <see cref="JobType"/> it services; the scheduler resolves the matching handler from a fresh DI scope,
/// so handlers may inject scoped services (AppDbContext, IFulfillmentQueue, ...) directly. Add a new
/// background job by adding a <see cref="JobType"/> value and a handler class registered as IJobHandler.
/// </summary>
public interface IJobHandler
{
    JobType Type { get; }
    Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct);
}

/// <summary>Context passed to a handler for one execution.</summary>
/// <param name="Schedule">The schedule row that triggered this run (null for an ad-hoc run).</param>
/// <param name="Manual">True when an admin pressed "Run now" rather than the interval firing.</param>
public record JobContext(ScheduledJobEntity? Schedule, bool Manual);

/// <summary>The outcome of a single job execution, persisted to the run history.</summary>
public record JobResult(JobRunStatus Status, int ItemsProcessed, string? Message)
{
    public static JobResult Ok(int itemsProcessed, string? message = null) =>
        new(JobRunStatus.Succeeded, itemsProcessed, message);
    /// <summary>Ran but there was nothing to do (nothing due, or the feature is disabled) — not an error.</summary>
    public static JobResult Skipped(string? message = null) => new(JobRunStatus.Skipped, 0, message);
    public static JobResult Failed(string message) => new(JobRunStatus.Failed, 0, message);
}
