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
        { FulfillmentStatus.Queued, FulfillmentStatus.Claimed, FulfillmentStatus.Downloading };

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

        var views = new List<DownloadJobView>(jobs.Count);
        foreach (var j in jobs)
        {
            var isActive = Array.IndexOf(Active, j.Status) >= 0;
            // Live per-torrent rows only matter while in flight; terminal jobs show their final job status.
            var torrents = isActive ? telemetry.Get(j.Id).ToList() : new List<DownloadTorrentTelemetry>();
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
                Stage = StageLabel(j.Status, torrents),
                Torrents = torrents
            });
        }
        return views;
    }

    // Human lifecycle label spanning approved → downloading → renaming/moving → available.
    private static string StageLabel(FulfillmentStatus status, List<DownloadTorrentTelemetry> torrents) => status switch
    {
        FulfillmentStatus.Queued => "Approved — queued",
        FulfillmentStatus.Claimed => "Starting download",
        FulfillmentStatus.Downloading when torrents.Any(t => t.Stage == DownloadTorrentStage.Importing)
            => "Renaming & moving",
        FulfillmentStatus.Downloading when torrents.Count > 0 && torrents.All(t => t.Stage is DownloadTorrentStage.Finishing or DownloadTorrentStage.Imported)
            => "Finishing",
        FulfillmentStatus.Downloading => "Downloading",
        FulfillmentStatus.Completed => "Available",
        FulfillmentStatus.PartiallyCompleted => "Partially available",
        FulfillmentStatus.Failed => "Failed",
        FulfillmentStatus.Cancelled => "Cancelled",
        _ => status.ToString()
    };
}
