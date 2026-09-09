using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

public interface IMediaMetadataScanService
{
    Task<MediaMetadataScanQueueResultDto> QueueAsync(MediaMetadataScanRequestDto request,
        CancellationToken ct = default);
    Task<MediaMetadataScanTaskDto?> ClaimAsync(string workerId, CancellationToken ct);
    Task<bool> ReportAsync(MediaMetadataScanReportDto report, CancellationToken ct);
}

/// <summary>Durable coordination for administrator-requested MediaInfo inspection. This service changes
/// only audit metadata; it does not enqueue fulfillment work and has no API for moving, replacing, or
/// changing the contents of a library file.</summary>
public sealed class MediaMetadataScanService(
    AppDbContext db,
    ILogger<MediaMetadataScanService> logger) : IMediaMetadataScanService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromHours(2);
    private static readonly FulfillmentStatus[] ActiveJobs =
        [FulfillmentStatus.Queued, FulfillmentStatus.Claimed, FulfillmentStatus.Downloading, FulfillmentStatus.Deferred];

    public async Task<MediaMetadataScanQueueResultDto> QueueAsync(MediaMetadataScanRequestDto request,
        CancellationToken ct = default)
    {
        var requestIds = (request.RequestIds ?? []).Where(id => id > 0).Distinct().Take(200).ToList();
        var importedFileIds = (request.ImportedFileIds ?? []).Where(id => id > 0).Distinct().Take(2000).ToList();
        if (requestIds.Count == 0 && importedFileIds.Count == 0)
            return new MediaMetadataScanQueueResultDto { Message = "Choose at least one title with missing codec data." };

        var fileRequestIds = importedFileIds.Count == 0
            ? []
            : await db.ImportedFiles.AsNoTracking()
                .Where(file => importedFileIds.Contains(file.Id) && file.FulfillmentJob != null)
                .Select(file => file.FulfillmentJob!.MediaRequestId).Distinct().ToListAsync(ct);
        var scopeRequestIds = requestIds.Concat(fileRequestIds).Distinct().ToList();
        var activeRequestIds = await db.FulfillmentJobs.AsNoTracking()
            .Where(job => scopeRequestIds.Contains(job.MediaRequestId) && ActiveJobs.Contains(job.Status))
            .Select(job => job.MediaRequestId).Distinct().ToListAsync(ct);
        var active = activeRequestIds.ToHashSet();
        var files = await db.ImportedFiles
            .Include(file => file.FulfillmentJob)
            .ThenInclude(job => job!.MediaRequest)
            .Where(file => file.FileType == "video"
                && file.FulfillmentJob != null
                && scopeRequestIds.Contains(file.FulfillmentJob.MediaRequestId))
            .ToListAsync(ct);
        var current = files.GroupBy(file => file.DestinationPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(file => file.ImportedAt)
                .ThenByDescending(file => file.Id).First())
            .Where(file => requestIds.Contains(file.FulfillmentJob!.MediaRequestId)
                || importedFileIds.Contains(file.Id))
            .Where(file => file.FulfillmentJob?.MediaRequest is
                { Status: RequestStatus.Available, MediaType: not MediaType.Music })
            .Where(file => !HasObservedCodec(file))
            .ToList();

        var now = DateTime.UtcNow;
        var queued = 0;
        var alreadyRunning = 0;
        var busy = 0;
        foreach (var file in current)
        {
            if (active.Contains(file.FulfillmentJob!.MediaRequestId))
            {
                busy++;
                continue;
            }
            if (file.MediaMetadataScanStatus is MediaMetadataScanStatus.Queued
                or MediaMetadataScanStatus.Claimed)
            {
                alreadyRunning++;
                continue;
            }

            file.MediaMetadataScanStatus = MediaMetadataScanStatus.Queued;
            file.MediaMetadataScanRequestedAt = now;
            file.MediaMetadataScanClaimedAt = null;
            file.MediaMetadataScanClaimedBy = null;
            file.MediaMetadataScanCompletedAt = null;
            file.MediaMetadataScanDetail = null;
            queued++;
        }

        if (queued > 0) await db.SaveChangesAsync(ct);
        var success = queued > 0;
        var detail = success
            ? $"Queued {queued} file{(queued == 1 ? string.Empty : "s")} for read-only codec inspection."
              + (busy > 0 ? $" {busy} busy file{(busy == 1 ? " was" : "s were")} skipped." : string.Empty)
              + (alreadyRunning > 0 ? $" {alreadyRunning} already in progress." : string.Empty)
            : alreadyRunning > 0
                ? "Every matching file is already queued or being inspected."
                : busy > 0
                    ? "Matching titles currently have download or replacement work. Try again after it finishes."
                    : "No current imported files in this scope are missing codec data.";
        return new MediaMetadataScanQueueResultDto
        {
            Success = success,
            QueuedCount = queued,
            AlreadyRunningCount = alreadyRunning,
            BusyCount = busy,
            Message = detail
        };
    }

    public async Task<MediaMetadataScanTaskDto?> ClaimAsync(string workerId, CancellationToken ct)
    {
        workerId = Trim(workerId, 128) ?? "downloader";
        var now = DateTime.UtcNow;
        var staleBefore = now - ClaimTimeout;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var id = await db.ImportedFiles.AsNoTracking()
                .Where(file => file.FileType == "video"
                    && (file.MediaMetadataScanStatus == MediaMetadataScanStatus.Queued
                        || (file.MediaMetadataScanStatus == MediaMetadataScanStatus.Claimed
                            && file.MediaMetadataScanClaimedAt < staleBefore))
                    && !db.ImportedFiles.Any(later => later.DestinationPath == file.DestinationPath
                        && later.Id > file.Id)
                    && !db.FulfillmentJobs.Any(active => active.MediaRequestId == file.FulfillmentJob!.MediaRequestId
                        && ActiveJobs.Contains(active.Status)))
                .OrderBy(file => file.MediaMetadataScanRequestedAt)
                .ThenBy(file => file.Id)
                .Select(file => file.Id)
                .FirstOrDefaultAsync(ct);
            if (id == 0) return null;

            var claimed = await db.ImportedFiles
                .Where(file => file.Id == id
                    && (file.MediaMetadataScanStatus == MediaMetadataScanStatus.Queued
                        || (file.MediaMetadataScanStatus == MediaMetadataScanStatus.Claimed
                            && file.MediaMetadataScanClaimedAt < staleBefore))
                    && !db.ImportedFiles.Any(later => later.DestinationPath == file.DestinationPath
                        && later.Id > file.Id)
                    && !db.FulfillmentJobs.Any(active => active.MediaRequestId == file.FulfillmentJob!.MediaRequestId
                        && ActiveJobs.Contains(active.Status)))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(file => file.MediaMetadataScanStatus, MediaMetadataScanStatus.Claimed)
                    .SetProperty(file => file.MediaMetadataScanClaimedAt, now)
                    .SetProperty(file => file.MediaMetadataScanClaimedBy, workerId)
                    .SetProperty(file => file.MediaMetadataScanAttempts,
                        file => file.MediaMetadataScanAttempts + 1), ct);
            if (claimed == 0) continue;

            var row = await db.ImportedFiles.AsNoTracking()
                .Include(file => file.FulfillmentJob)
                .ThenInclude(job => job!.MediaRequest)
                .SingleAsync(file => file.Id == id, ct);
            var job = row.FulfillmentJob!;
            return new MediaMetadataScanTaskDto
            {
                ImportedFileId = row.Id,
                DestinationPath = row.DestinationPath,
                Title = job.Title,
                MediaType = job.MediaType,
                Quality = job.Quality,
                Genres = Split(job.GenresCsv),
                IsAnime = job.IsAnime || job.MediaRequest?.IsAnime == true || job.MediaType == MediaType.Anime,
                LibraryDestinationRootPath = job.LibraryDestinationRootPath
            };
        }

        return null;
    }

    public async Task<bool> ReportAsync(MediaMetadataScanReportDto report, CancellationToken ct)
    {
        if (report.ImportedFileId <= 0 || string.IsNullOrWhiteSpace(report.WorkerId)) return false;
        var workerId = Trim(report.WorkerId, 128);
        var row = await db.ImportedFiles.FirstOrDefaultAsync(file => file.Id == report.ImportedFileId
            && file.MediaMetadataScanStatus == MediaMetadataScanStatus.Claimed
            && file.MediaMetadataScanClaimedBy == workerId, ct);
        if (row is null) return false;

        var video = report.MediaTracks?.Video.FirstOrDefault();
        var succeeded = report.Succeeded && report.MediaTracks?.HasVideo == true
            && VideoCodecPolicy.Normalize(video?.Codec) is not null;
        row.MediaMetadataScanStatus = succeeded
            ? MediaMetadataScanStatus.Succeeded
            : MediaMetadataScanStatus.Failed;
        row.MediaMetadataScanClaimedAt = null;
        row.MediaMetadataScanClaimedBy = null;
        row.MediaMetadataScanCompletedAt = DateTime.UtcNow;
        row.MediaMetadataScanDetail = Trim(succeeded
            ? report.Detail ?? $"Detected {VideoCodecPolicy.Display(video!.Codec)}"
            : report.Detail ?? "MediaInfo did not return a readable video codec", 2000);
        if (succeeded)
        {
            row.MediaTracksJson = JsonSerializer.Serialize(report.MediaTracks, Json);
            var quality = VideoResolutionPolicy.FromDimensions(video!.Width, video.Height);
            if (quality != Quality.Any) row.ResolutionHeight = (int)quality;
        }
        else
        {
            logger.LogWarning("Codec metadata inspection failed for imported file {FileId}: {Detail}",
                row.Id, row.MediaMetadataScanDetail);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private static bool HasObservedCodec(ImportedFileEntity file)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(file.MediaTracksJson)) return false;
            var tracks = JsonSerializer.Deserialize<MediaTrackSummaryDto>(file.MediaTracksJson, Json);
            return VideoCodecPolicy.Normalize(tracks?.Video.FirstOrDefault()?.Codec) is not null;
        }
        catch (JsonException) { return false; }
    }

    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Trim()[..Math.Min(max, value.Trim().Length)];

    private static List<string> Split(string? csv) => string.IsNullOrWhiteSpace(csv)
        ? []
        : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
