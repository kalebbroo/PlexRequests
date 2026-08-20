using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>Assembles the admin live-downloads read model from persisted jobs + in-memory telemetry.</summary>
public sealed class DownloadMonitorService(AppDbContext db, IDownloadTelemetryStore telemetry) : IDownloadMonitorService
{
    // Jobs the worker is still working on — always shown.
    private static readonly FulfillmentStatus[] Active =
        { FulfillmentStatus.Queued, FulfillmentStatus.Claimed, FulfillmentStatus.Downloading, FulfillmentStatus.Deferred };

    public async Task<List<DownloadJobView>> GetActiveAndRecentAsync(int recentMinutes = 30)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-Math.Max(0, recentMinutes));
        // Active jobs, plus any that reached a terminal state within the recent window (so the "finished,
        // imported, available" tail of the lifecycle stays visible for a while after completion).
        var jobs = await db.FulfillmentJobs
            .AsNoTracking()
            .Include(j => j.MediaRequest)
            .Where(j => Active.Contains(j.Status) || (j.CompletedAt != null && j.CompletedAt >= cutoff))
            .OrderByDescending(j => j.CompletedAt ?? j.LastUpdatedAt ?? j.CreatedAt)
            .ToListAsync();
        var jobIds = jobs.Select(job => job.Id).ToList();
        var persistedTransfers = jobIds.Count == 0
            ? new List<FulfillmentTransferEntity>()
            : await db.FulfillmentTransfers.AsNoTracking()
                .Where(transfer => jobIds.Contains(transfer.FulfillmentJobId))
                .OrderBy(transfer => transfer.Id)
                .ToListAsync();
        var persistedByJob = persistedTransfers
            .GroupBy(transfer => transfer.FulfillmentJobId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var views = new List<DownloadJobView>(jobs.Count);
        foreach (var j in jobs)
        {
            var isActive = Array.IndexOf(Active, j.Status) >= 0;
            var transfers = isActive ? telemetry.Get(j.Id).ToList() : new List<DownloadTransferTelemetry>();
            // The in-memory snapshot is the freshest source, but it is deliberately lost on a web/worker
            // restart. Fall back to durable tracking rows so the panel self-heals instead of turning blank,
            // and so recently completed jobs retain release/source details.
            if (transfers.Count == 0 && persistedByJob.TryGetValue(j.Id, out var persisted))
                transfers = persisted.Select(ToTelemetry).ToList();
            views.Add(new DownloadJobView
            {
                JobId = j.Id,
                MediaRequestId = j.MediaRequestId,
                Title = j.Title,
                Year = j.Year,
                MediaType = j.MediaType,
                PosterUrl = j.MediaRequest?.PosterUrl,
                RequestedBy = j.MediaRequest?.RequestedBy,
                Status = j.Status,
                Progress = j.Progress,
                Attempts = j.Attempts,
                LastError = j.LastError,
                UpdatedAt = j.CompletedAt ?? j.LastUpdatedAt ?? j.CreatedAt,
                IsActive = isActive,
                Stage = StageLabel(j.Status, transfers),
                Transfers = transfers
            });
        }
        return views;
    }

    private static DownloadTransferTelemetry ToTelemetry(FulfillmentTransferEntity transfer) => new()
    {
        Name = transfer.ReleaseName ?? transfer.TransferId,
        Protocol = transfer.Protocol,
        Source = transfer.Source,
        IndexerId = transfer.IndexerId,
        Stage = transfer.State switch
        {
            TransferTrackingState.Finished => DownloadTransferStage.Finishing,
            TransferTrackingState.Imported => DownloadTransferStage.Imported,
            TransferTrackingState.Failed => DownloadTransferStage.Failed,
            TransferTrackingState.Missing => DownloadTransferStage.Missing,
            _ => DownloadTransferStage.Downloading
        },
        ProgressPercent = transfer.Progress,
        DownloadRateBytesPerSec = transfer.DownloadRateBytesPerSec,
        Seeds = transfer.Seeds,
        Peers = transfer.Peers,
        TotalSizeBytes = transfer.TotalSizeBytes,
        Season = transfer.Season,
        Episode = transfer.Episode
    };

    // Human lifecycle label spanning approved → downloading → renaming/moving → available.
    private static string StageLabel(FulfillmentStatus status, List<DownloadTransferTelemetry> transfers) => status switch
    {
        FulfillmentStatus.Queued => "Approved — queued",
        FulfillmentStatus.Claimed => "Starting download",
        FulfillmentStatus.Downloading when transfers.Any(t => t.Stage == DownloadTransferStage.Importing)
            => "Renaming & moving",
        FulfillmentStatus.Downloading when transfers.Count > 0 && transfers.All(t => t.Stage is DownloadTransferStage.Finishing or DownloadTransferStage.Imported)
            => "Finishing",
        FulfillmentStatus.Downloading => "Downloading",
        FulfillmentStatus.Deferred => "Waiting for a release",
        FulfillmentStatus.Completed => "Available",
        FulfillmentStatus.PartiallyCompleted => "Partially available",
        FulfillmentStatus.Failed => "Failed",
        FulfillmentStatus.Cancelled => "Cancelled",
        _ => status.ToString()
    };
}
