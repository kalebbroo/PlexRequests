using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Api;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Download;
using PlexRequests.Downloader.Organize;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Worker;

/// <summary>Retries the cleanup phase of verified imports, removes only stale Plex Requests-owned temporary
/// artifacts, and publishes a storage heartbeat for the admin dashboard.</summary>
public sealed class StorageMaintenanceWorker(
    IPlexRequestsApiClient api,
    IAcquisitionBackendRegistry backends,
    IPostImportCleanup postImportCleanup,
    IStorageArtifactCleaner artifacts,
    IStorageSafetyService storage,
    IJobStateStore stateStore,
    ILibraryOrganizationProvider libraryPreferences,
    IOptions<StorageOptions> options,
    ILogger<StorageMaintenanceWorker> logger) : BackgroundService
{
    private readonly StorageOptions _options = options.Value;
    private DateTime _lastArtifactSweep = DateTime.MinValue;
    private StorageMaintenanceResult _lastArtifactResult = new([], 0, 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.StatusIntervalSeconds, 30, 600));
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceSafeAsync(stoppingToken);
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await libraryPreferences.RefreshAsync(ct);
        var pending = await api.GetPendingCleanupTransfersAsync(ct);
        var completedThisPass = 0;
        await Parallel.ForEachAsync(pending.Take(20), new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = 4
        }, async (transfer, token) =>
        {
            if (await RetryTransferCleanupAsync(transfer, token))
                Interlocked.Increment(ref completedThisPass);
        });

        var active = await stateStore.GetAllAsync(ct);
        var durableActive = await api.GetActiveTransfersAsync(ct);
        var roots = new List<string> { _options.WorkingPath };
        var preferences = libraryPreferences.Current;
        roots.AddRange(preferences.Destinations.Where(x => x.Enabled).Select(x => x.RootPath));
        if (preferences.Destinations.Count == 0)
            roots.AddRange([preferences.MoviePath, preferences.TvPath, preferences.MusicPath]);
        var scanInterval = TimeSpan.FromMinutes(Math.Clamp(_options.ArtifactScanIntervalMinutes, 5, 1440));
        if (DateTime.UtcNow - _lastArtifactSweep >= scanInterval)
        {
            var activeJobIds = active.Select(x => x.Job.Id)
                .Concat(durableActive.Select(x => x.FulfillmentJobId))
                .ToHashSet();
            _lastArtifactResult = artifacts.Sweep(roots, activeJobIds,
                TimeSpan.FromHours(Math.Clamp(preferences.StaleArtifactHours, 1, 168)),
                preferences.AutoCleanupStaleArtifacts);
            _lastArtifactSweep = DateTime.UtcNow;
        }
        storage.RecordMaintenance(_lastArtifactResult, Math.Max(0, pending.Count - completedThisPass));
        await api.ReportStorageStatusAsync(await storage.GetStatusAsync(ct), ct);
    }

    private async Task RunOnceSafeAsync(CancellationToken ct)
    {
        try { await RunOnceAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Storage maintenance pass failed; retrying next interval");
            storage.RecordMaintenance(new StorageMaintenanceResult([], 0, 0), 0, ex.Message);
            try { await api.ReportStorageStatusAsync(await storage.GetStatusAsync(ct), ct); }
            catch { }
        }
    }

    private async Task<bool> RetryTransferCleanupAsync(TrackedTransferDto transfer, CancellationToken ct)
    {
        if (!backends.TryGet(transfer.Protocol, out var backend))
            return await Report(false, $"No {transfer.Protocol} backend is available");
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var status = await backend.GetStatusAsync(transfer.TransferId, attempt.Token);
            if (status is null)
                return await Report(true, null); // already absent is the desired idempotent state

            var audit = await api.GetImportedFilesAsync(transfer.FulfillmentJobId, attempt.Token);
            var files = audit?.Where(x => x.Protocol == transfer.Protocol
                && string.Equals(x.TransferId, transfer.TransferId, StringComparison.OrdinalIgnoreCase))
                .Select(x => new ImportedFileRecord(x.SourcePath, x.DestinationPath, x.FileType,
                    x.SeasonNumber, x.EpisodeNumber, x.SizeBytes, x.MediaTracks, x.EpisodeCoverage))
                .ToList() ?? [];
            if (files.Count == 0)
                return await Report(false, "No verified import audit is available; source retained");

            var cleaned = await postImportCleanup.RunAsync(transfer.Protocol, transfer.TransferId,
                ImportResult.Ok(files), attempt.Token);
            return await Report(cleaned, cleaned ? null : "Backend cleanup was declined; it will be retried");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return await Report(false, "Cleanup attempt timed out; source retained");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Retry cleanup failed for transfer {TransferId}", transfer.TransferId);
            return await Report(false, ex.Message);
        }

        async Task<bool> Report(bool completed, string? error)
        {
            await api.ReportTransferCleanupAsync(new TransferCleanupReportDto(
                transfer.FulfillmentJobId, transfer.Protocol, transfer.TransferId, completed, error), ct);
            return completed;
        }
    }
}
