using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>Assembles the admin live-downloads read model from persisted jobs + in-memory telemetry.</summary>
public sealed class DownloadMonitorService(
    AppDbContext db,
    IDownloadTelemetryStore telemetry,
    IStorageTelemetryStore storageTelemetry) : IDownloadMonitorService
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
            .ToListAsync();
        // Progress heartbeats update LastUpdatedAt every few seconds. Sorting on that value made cards trade
        // places on every poll even though nothing meaningful about their lifecycle had changed. Keep each
        // lifecycle lane deterministic instead: transfers first in the order they started, queued work next,
        // the recent terminal tail after that, and release searches parked at the bottom.
        jobs = jobs
            .OrderBy(j => DisplayRank(j.Status))
            .ThenBy(j => j.ClaimedAt ?? j.CreatedAt)
            .ThenBy(j => j.Id)
            .ToList();
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
        var formatNames = await db.CustomFormats.AsNoTracking().ToDictionaryAsync(format => format.Id, format => format.Name);

        var views = new List<DownloadJobView>(jobs.Count);
        foreach (var j in jobs)
        {
            var optimization = ParseOptimization(j.StorageOptimizationPolicyJson);
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
                Stage = StageLabel(j.Status, transfers, optimization is not null),
                IsStorageOptimization = optimization is not null,
                OptimizationTargetFileCount = optimization?.Targets.Count ?? 0,
                OptimizationSeasons = optimization?.Targets.SelectMany(target => target.EpisodeCoverage)
                    .Select(episode => episode.Season).Distinct().Order().ToList() ?? [],
                OptimizationGoals = optimization is null ? [] : DescribeGoals(optimization, formatNames),
                OptimizationOriginalBytes = optimization?.CurrentBytes,
                OptimizationReplacementBytes = j.StorageOptimizationReplacementBytes,
                Transfers = transfers
            });
        }
        return views;
    }

    public StorageStatusDto? GetStorageStatus() => storageTelemetry.Get();

    public async Task<PlaybackPreparationStatusDto> GetPlaybackPreparationStatusAsync()
    {
        const int maxAttempts = 3;
        var state = await db.ImportedFiles.AsNoTracking()
            .Where(file => file.FileType == "video"
                && EF.Functions.Like(file.DestinationPath, "%.mkv")
                && file.PlaybackPreparedAt == null
                && !db.ImportedFiles.Any(later => later.DestinationPath == file.DestinationPath
                    && later.Id > file.Id))
            .GroupBy(_ => 1)
            .Select(group => new PlaybackPreparationStatusDto
            {
                PendingCount = group.Count(file => (file.PlaybackPreparationAttempts < maxAttempts
                        || (file.PlaybackPreparationAttempts == maxAttempts
                            && file.PlaybackPreparationDetail == PlaybackPreparationReportDto.LegacyOutsideRootFailure))
                    && file.PlaybackPreparationClaimedAt == null),
                InProgressCount = group.Count(file => file.PlaybackPreparationClaimedAt != null),
                FailedCount = group.Count(file => file.PlaybackPreparationAttempts >= maxAttempts
                    && file.PlaybackPreparationDetail != PlaybackPreparationReportDto.LegacyOutsideRootFailure)
            })
            .FirstOrDefaultAsync();
        return state ?? new PlaybackPreparationStatusDto();
    }

    private static int DisplayRank(FulfillmentStatus status) => status switch
    {
        FulfillmentStatus.Claimed or FulfillmentStatus.Downloading => 0,
        FulfillmentStatus.Queued => 1,
        FulfillmentStatus.Completed or FulfillmentStatus.PartiallyCompleted or
            FulfillmentStatus.Failed or FulfillmentStatus.Cancelled => 2,
        FulfillmentStatus.Deferred => 3,
        _ => 2
    };

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
    private static string StageLabel(FulfillmentStatus status, List<DownloadTransferTelemetry> transfers,
        bool optimization) => (status, optimization) switch
        {
            (FulfillmentStatus.Queued, true) => "Optimization queued",
            (FulfillmentStatus.Claimed, true) => "Searching for optimized release",
            (FulfillmentStatus.Downloading, true) when transfers.Any(t => t.Stage == DownloadTransferStage.Importing)
                => "Verifying & replacing",
            (FulfillmentStatus.Downloading, true) when transfers.Count > 0
                                                           && transfers.All(t => t.Stage is DownloadTransferStage.Finishing
                                                               or DownloadTransferStage.Imported)
                => "Verifying replacement",
            (FulfillmentStatus.Downloading, true) => "Downloading replacement",
            (FulfillmentStatus.Deferred, true) => "Waiting for matching release",
            (FulfillmentStatus.Completed, true) => "Optimized",
            (FulfillmentStatus.PartiallyCompleted, true) => "Waiting for remaining replacements",
            (FulfillmentStatus.Failed, true) => "Optimization needs attention",
            (FulfillmentStatus.Cancelled, true) => "Optimization cancelled",
            (FulfillmentStatus.Queued, false) => "Approved — queued",
            (FulfillmentStatus.Claimed, false) => "Starting download",
            (FulfillmentStatus.Downloading, false) when transfers.Any(t => t.Stage == DownloadTransferStage.Importing)
                => "Renaming & moving",
            (FulfillmentStatus.Downloading, false) when transfers.Count > 0
                                                            && transfers.All(t => t.Stage is DownloadTransferStage.Finishing
                                                                or DownloadTransferStage.Imported)
                => "Finishing",
            (FulfillmentStatus.Downloading, false) => "Downloading",
            (FulfillmentStatus.Deferred, false) => "Waiting for a release",
            (FulfillmentStatus.Completed, false) => "Available",
            (FulfillmentStatus.PartiallyCompleted, false) => "Partially available",
            (FulfillmentStatus.Failed, false) => "Failed",
            (FulfillmentStatus.Cancelled, false) => "Cancelled",
            _ => status.ToString()
        };

    private static StorageOptimizationPolicyDto? ParseOptimization(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<StorageOptimizationPolicyDto>(json); }
        catch (JsonException) { return null; }
    }

    private static List<string> DescribeGoals(StorageOptimizationPolicyDto policy,
        IReadOnlyDictionary<int, string> formatNames)
    {
        var goals = new List<string>();
        if (!string.IsNullOrWhiteSpace(policy.RequiredVideoCodec))
            goals.Add(VideoCodecPolicy.Display(policy.RequiredVideoCodec));
        if (policy.TargetQuality != Quality.Any) goals.Add(policy.TargetQuality.Label());
        goals.AddRange(policy.RequiredCustomFormatIds.Distinct()
            .Select(id => formatNames.TryGetValue(id, out var name) ? name : $"Format #{id}"));
        if (policy.MinimumSavingsPercent > 0) goals.Add($"Save at least {policy.MinimumSavingsPercent}%");
        return goals;
    }
}
