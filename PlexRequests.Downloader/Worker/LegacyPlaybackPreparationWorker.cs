using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Api;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Organize;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Worker;

internal enum PlaybackPreparationPass
{
    Idle,
    Completed,
    Changed,
    Deferred
}

internal sealed class RetiredLibraryPathException(string message) : InvalidOperationException(message);

/// <summary>Gradually brings Plex Requests-managed legacy MKV files under the same playback-order guarantee
/// as new imports. Work is claimed durably, performed one file at a time, and committed by mkvmerge's
/// same-directory atomic replacement; torrent payloads and files outside configured library roots are never
/// touched.</summary>
internal sealed class LegacyPlaybackPreparationWorker(
    IPlexRequestsApiClient api,
    IMediaTrackInspector trackInspector,
    ILibraryOrganizationProvider libraryPreferences,
    IStorageVolumeProbe volumes,
    IStorageSafetyService storage,
    IOptions<WorkerOptions> worker,
    ILogger<LegacyPlaybackPreparationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InspectionDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ChangedDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(5);
    private readonly string _workerId = worker.Value.WorkerId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await RunOnceSafeAsync(stoppingToken);
            var delay = result switch
            {
                PlaybackPreparationPass.Completed => InspectionDelay,
                PlaybackPreparationPass.Changed => ChangedDelay,
                _ => IdleDelay
            };
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task<PlaybackPreparationPass> RunOnceAsync(CancellationToken ct)
    {
        await libraryPreferences.RefreshAsync(ct);
        var task = await api.ClaimPlaybackPreparationAsync(_workerId, ct);
        if (task is null) return PlaybackPreparationPass.Idle;

        try
        {
            var preferences = libraryPreferences.Current;
            var (path, root) = ResolveLibraryPath(task, preferences);
            var volume = volumes.Read(root);
            if (!volume.IsReady)
            {
                await ReportAsync(task, completed: false, changed: false, attempted: false,
                    $"Library filesystem is unavailable: {volume.Error}", null, ct);
                return PlaybackPreparationPass.Deferred;
            }
            if (!File.Exists(path))
            {
                await ReportAsync(task, completed: true, changed: false, attempted: false,
                    "Library file is no longer present; no repair is required", null, ct);
                return PlaybackPreparationPass.Completed;
            }

            var before = await trackInspector.InspectAsync(path, [], ct);
            if (!before.HasVideo)
                throw new InvalidOperationException("The library file contains no readable video stream");
            var selection = MediaTrackDefaultSelection.Create(task.Policy, before, task.IsAnime);
            if (selection is null || IsPrepared(before, selection))
            {
                await ReportAsync(task, completed: true, changed: false, attempted: false,
                    "Playback order already satisfies the saved media policy", before, ct);
                return PlaybackPreparationPass.Completed;
            }

            var size = new FileInfo(path).Length;
            await using (var lease = await storage.TryReserveLibraryRewriteAsync(task.ImportedFileId,
                task.Title, size, root, preferences, ct))
            {
                if (!lease.Admission.Allowed)
                {
                    await ReportAsync(task, completed: false, changed: false, attempted: false,
                        lease.Admission.Detail, before, ct);
                    return PlaybackPreparationPass.Deferred;
                }

                Prepare(path, selection);
            }

            var after = await trackInspector.InspectAsync(path, [], ct);
            var verification = MediaTrackDefaultSelection.Create(task.Policy, after, task.IsAnime);
            if (verification is not null && !IsPrepared(after, verification))
                throw new InvalidOperationException("Playback preparation completed but verification still found the preferred stream out of order");

            var reported = await ReportAsync(task, completed: true, changed: true, attempted: true,
                "Preferred playback streams were normalized losslessly", after, ct);
            if (reported) await api.RefreshLibraryAsync(task.MediaType, ct);
            logger.LogInformation("Normalized legacy playback order for {Title}: {File}",
                task.Title, Path.GetFileName(path));
            return PlaybackPreparationPass.Changed;
        }
        catch (RetiredLibraryPathException ex)
        {
            logger.LogInformation("Skipping legacy playback preparation for retired library path {Path}: {Detail}",
                task.DestinationPath, ex.Message);
            await ReportAsync(task, completed: true, changed: false, attempted: false,
                $"Retired library path skipped: {ex.Message}", null, ct);
            return PlaybackPreparationPass.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Legacy playback preparation failed for imported file {FileId} ({Title})",
                task.ImportedFileId, task.Title);
            await ReportAsync(task, completed: false, changed: false, attempted: true,
                ex.Message, null, CancellationToken.None);
            return PlaybackPreparationPass.Deferred;
        }

        void Prepare(string path, MediaTrackDefaultSelection selection)
        {
            if (!trackInspector.PrepareExistingLibraryFile(path, selection))
                throw new InvalidOperationException("MKV playback preparation was not applied");
        }
    }

    private async Task<PlaybackPreparationPass> RunOnceSafeAsync(CancellationToken ct)
    {
        try { return await RunOnceAsync(ct); }
        catch (OperationCanceledException) { return PlaybackPreparationPass.Idle; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Legacy playback preparation pass failed; retrying later");
            return PlaybackPreparationPass.Deferred;
        }
    }

    private Task<bool> ReportAsync(PlaybackPreparationTaskDto task, bool completed, bool changed,
        bool attempted, string detail, MediaTrackSummaryDto? tracks, CancellationToken ct) =>
        api.ReportPlaybackPreparationAsync(new PlaybackPreparationReportDto
        {
            ImportedFileId = task.ImportedFileId,
            WorkerId = _workerId,
            Completed = completed,
            Changed = changed,
            Attempted = attempted,
            Detail = detail,
            MediaTracks = tracks
        }, ct);

    internal static bool RequiresFullRewrite(MediaTrackDefaultSelection selection) =>
        selection.AudioOrdinal is > 1
        || selection.EditSubtitles && selection.SubtitleOrdinal is > 1;

    internal static bool IsPrepared(MediaTrackSummaryDto tracks, MediaTrackDefaultSelection selection)
    {
        var audio = tracks.Audio.Where(track => !track.IsExternal).ToList();
        if (selection.AudioOrdinal is int audioOrdinal
            && (audioOrdinal != 1 || audio.Count(track => track.IsDefault) != 1 || !audio[0].IsDefault))
            return false;

        if (!selection.EditSubtitles) return true;
        var subtitles = tracks.Subtitles.Where(track => !track.IsExternal).ToList();
        return selection.SubtitleOrdinal is int subtitleOrdinal
            ? subtitleOrdinal == 1 && subtitles.Count(track => track.IsDefault) == 1 && subtitles[0].IsDefault
            : subtitles.All(track => !track.IsDefault);
    }

    internal static (string Path, string Root) ResolveLibraryPath(PlaybackPreparationTaskDto task,
        EffectiveLibraryOrganization preferences)
    {
        var destinationPath = task.DestinationPath;
        if (string.IsNullOrWhiteSpace(destinationPath)
            || !Path.GetExtension(destinationPath).Equals(".mkv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only audited MKV library files can be prepared");

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var roots = preferences.Destinations
            .Where(destination => destination.Enabled)
            .Select(destination => destination.RootPath)
            .Concat([preferences.MoviePath, preferences.TvPath, preferences.MusicPath])
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(comparison == StringComparison.OrdinalIgnoreCase
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .OrderByDescending(root => root.Length);
        var fullPath = Path.IsPathFullyQualified(destinationPath)
            ? Path.GetFullPath(destinationPath)
            : ResolveLegacyRelativePath(task, preferences, roots, comparison);
        foreach (var root in roots)
        {
            if (IsWithin(fullPath, root, comparison)) return (fullPath, root);
        }

        throw new RetiredLibraryPathException(PlaybackPreparationReportDto.LegacyOutsideRootFailure);
    }

    private static string ResolveLegacyRelativePath(PlaybackPreparationTaskDto task,
        EffectiveLibraryOrganization preferences, IEnumerable<string> configuredRoots,
        StringComparison comparison)
    {
        var selectedRoot = task.LibraryDestinationRootPath;
        if (string.IsNullOrWhiteSpace(selectedRoot))
            selectedRoot = preferences.Resolve(task.MediaType, task.Quality, task.Genres,
                task.IsAnime, isEpisode: true).Root;
        if (string.IsNullOrWhiteSpace(selectedRoot))
            throw new InvalidOperationException("The legacy relative audit path has no configured library destination");

        selectedRoot = Path.GetFullPath(selectedRoot);
        if (!configuredRoots.Any(root => string.Equals(Path.TrimEndingDirectorySeparator(root),
                Path.TrimEndingDirectorySeparator(selectedRoot), comparison)))
            throw new RetiredLibraryPathException("The legacy audit destination is no longer configured");

        var fullPath = Path.GetFullPath(Path.Combine(selectedRoot, task.DestinationPath));
        if (!IsWithin(fullPath, selectedRoot, comparison))
            throw new InvalidOperationException("The legacy relative audit path escapes its library root");
        return fullPath;
    }

    private static bool IsWithin(string path, string root, StringComparison comparison)
    {
        var normalized = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalized, comparison);
    }
}
