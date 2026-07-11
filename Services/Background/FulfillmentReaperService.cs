using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Background;

/// <summary>
/// Backstop for dead workers: periodically finds jobs stuck in Claimed/Downloading with no recent
/// activity and either requeues them (another worker retries) or fails them after too many attempts,
/// so a crashed downloader never strands a request forever.
/// </summary>
public class FulfillmentReaperService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<FulfillmentReaperService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue<bool>("Fulfillment:Enabled"))
        {
            logger.LogInformation("Fulfillment disabled; stale-claim reaper not running");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, config.GetValue("Fulfillment:ReaperIntervalMinutes", 5)));
        var staleMinutes = Math.Max(1, config.GetValue("Fulfillment:StaleMinutes", 30));
        var maxAttempts = Math.Max(1, config.GetValue("Fulfillment:MaxAttempts", 3));
        logger.LogInformation("Stale-claim reaper started (every {Interval}m, stale after {Stale}m, max {Max} attempts)",
            interval.TotalMinutes, staleMinutes, maxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(staleMinutes, maxAttempts, stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Reaper pass failed"); }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Run a single reap pass. Returns the number of stale jobs acted on.</summary>
    public async Task<int> RunOnceAsync(int staleMinutes, int maxAttempts, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notify = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var cutoff = DateTime.UtcNow.AddMinutes(-staleMinutes);
        var stale = await db.FulfillmentJobs
            .Where(j => (j.Status == FulfillmentStatus.Claimed || j.Status == FulfillmentStatus.Downloading)
                        && (j.LastUpdatedAt == null || j.LastUpdatedAt < cutoff))
            .ToListAsync(ct);

        foreach (var job in stale)
        {
            var req = await db.MediaRequests.FirstOrDefaultAsync(r => r.Id == job.MediaRequestId, ct);

            if (job.Attempts >= maxAttempts)
            {
                job.Status = FulfillmentStatus.Failed;
                job.LastError = $"No progress after {job.Attempts} attempt(s); reaper gave up.";
                job.CompletedAt = DateTime.UtcNow;
                if (req is not null && req.Status != RequestStatus.Available)
                {
                    req.Status = RequestStatus.Failed;
                    req.DenialReason = "Fulfillment timed out";
                }
                await db.SaveChangesAsync(ct);
                if (req is not null) await notify.RequestFailedAsync(ToDto(req), "Fulfillment timed out");
                logger.LogWarning("Reaped job {JobId} as Failed (>= {Max} attempts)", job.Id, maxAttempts);
            }
            else
            {
                job.Status = FulfillmentStatus.Queued;
                job.ClaimedBy = null;
                job.ClaimedAt = null;
                job.LastUpdatedAt = DateTime.UtcNow;
                if (req is not null && req.Status == RequestStatus.Processing)
                    req.Status = RequestStatus.Approved;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Requeued stale job {JobId} (attempt {N})", job.Id, job.Attempts);
            }
        }

        if (stale.Count > 0) logger.LogInformation("Reaper acted on {Count} stale job(s)", stale.Count);
        return stale.Count;
    }

    private static MediaRequestDto ToDto(MediaRequestEntity r) => new()
    {
        Id = r.Id,
        MediaId = r.MediaId,
        MediaType = r.MediaType,
        Title = r.Title,
        Status = r.Status,
        RequestedByUserId = r.RequestedByUserId ?? 0,
        RequestedByUsername = r.RequestedBy ?? string.Empty,
        DenialReason = r.DenialReason
    };
}
