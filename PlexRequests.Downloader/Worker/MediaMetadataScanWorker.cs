using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Api;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Organize;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Worker;

internal enum MediaMetadataScanPass
{
    Idle,
    Completed,
    Failed
}

/// <summary>Runs explicit MediaInfo backfills one file at a time. It calls only the inspector's read method,
/// never track preparation, the organizer, Plex refresh, or any download API, so both the media bytes and
/// the surrounding library remain unchanged.</summary>
internal sealed class MediaMetadataScanWorker(
    IPlexRequestsApiClient api,
    IMediaTrackInspector trackInspector,
    ILibraryOrganizationProvider libraryPreferences,
    IStorageVolumeProbe volumes,
    IOptions<WorkerOptions> worker,
    ILogger<MediaMetadataScanWorker> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WorkDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(1);
    private readonly string _workerId = worker.Value.WorkerId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await RunOnceSafeAsync(stoppingToken);
            try { await Task.Delay(result == MediaMetadataScanPass.Idle ? IdleDelay : WorkDelay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task<MediaMetadataScanPass> RunOnceAsync(CancellationToken ct)
    {
        await libraryPreferences.RefreshAsync(ct);
        var task = await api.ClaimMediaMetadataScanAsync(_workerId, ct);
        if (task is null) return MediaMetadataScanPass.Idle;

        try
        {
            var (path, root) = LibraryAuditPathResolver.Resolve(new LibraryAuditPathContext(
                task.DestinationPath, task.LibraryDestinationRootPath, task.MediaType,
                task.Quality, task.Genres, task.IsAnime), libraryPreferences.Current);
            var volume = volumes.Read(root);
            if (!volume.IsReady)
                return await FailAsync(task, $"Library filesystem is unavailable: {volume.Error}",
                    retryable: true, ct: ct);
            if (!File.Exists(path))
                return await FailAsync(task, "The audited library file is no longer present",
                    retryable: false, ct: ct);

            var tracks = await trackInspector.InspectAsync(path, [], ct);
            var video = tracks.Video.FirstOrDefault();
            if (!tracks.HasVideo || video is null || VideoCodecPolicy.Normalize(video.Codec) is null)
                return await FailAsync(task, "MediaInfo did not return a readable video codec",
                    retryable: false, ct: ct);

            var quality = VideoResolutionPolicy.FromDimensions(video.Width, video.Height);
            var detail = $"Detected {VideoCodecPolicy.Display(video.Codec)}"
                         + (quality != PlexRequestsHosted.Shared.Enums.Quality.Any
                             ? $" at {quality.Label()}"
                             : string.Empty);
            var reported = await ReportWithRetryAsync(new MediaMetadataScanReportDto
            {
                ImportedFileId = task.ImportedFileId,
                WorkerId = _workerId,
                Succeeded = true,
                Detail = detail,
                MediaTracks = tracks
            }, ct);
            if (!reported)
            {
                logger.LogWarning("Could not persist codec metadata scan for imported file {FileId}",
                    task.ImportedFileId);
                return MediaMetadataScanPass.Failed;
            }

            logger.LogInformation("Read-only codec scan completed for {Title}: {File} ({Detail})",
                task.Title, Path.GetFileName(path), detail);
            return MediaMetadataScanPass.Completed;
        }
        catch (RetiredLibraryPathException ex)
        {
            return await FailAsync(task, $"Retired library path skipped: {ex.Message}",
                retryable: false, ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Read-only codec scan failed for imported file {FileId} ({Title})",
                task.ImportedFileId, task.Title);
            return await FailAsync(task, ex.Message, retryable: true, ct: CancellationToken.None);
        }
    }

    private async Task<MediaMetadataScanPass> FailAsync(MediaMetadataScanTaskDto task, string detail,
        bool retryable, CancellationToken ct)
    {
        var reported = await ReportWithRetryAsync(new MediaMetadataScanReportDto
        {
            ImportedFileId = task.ImportedFileId,
            WorkerId = _workerId,
            Succeeded = false,
            Retryable = retryable,
            Detail = detail
        }, ct);
        if (!reported)
            logger.LogWarning("Could not persist failed codec scan result for imported file {FileId}; "
                              + "the server will reclaim it after the claim lease expires", task.ImportedFileId);
        return MediaMetadataScanPass.Failed;
    }

    private async Task<bool> ReportWithRetryAsync(MediaMetadataScanReportDto report, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (await api.ReportMediaMetadataScanAsync(report, ct)) return true;
            if (attempt == 3) break;
            try { await Task.Delay(TimeSpan.FromSeconds(attempt), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        }
        return false;
    }

    private async Task<MediaMetadataScanPass> RunOnceSafeAsync(CancellationToken ct)
    {
        try { return await RunOnceAsync(ct); }
        catch (OperationCanceledException) { return MediaMetadataScanPass.Idle; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Media metadata scan pass failed; retrying later");
            return MediaMetadataScanPass.Failed;
        }
    }
}
