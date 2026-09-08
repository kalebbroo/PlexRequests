using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Worker;

internal sealed record StorageVolumeReading(
    string Id,
    string Path,
    long TotalBytes,
    long FreeBytes,
    bool IsReady,
    string? Error = null);

internal interface IStorageVolumeProbe
{
    StorageVolumeReading Read(string path);
}

internal sealed class PhysicalStorageVolumeProbe : IStorageVolumeProbe
{
    public StorageVolumeReading Read(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return new("missing", path ?? string.Empty, 0, 0, false, "Storage path is not configured");
            var full = Path.GetFullPath(path);
            var existing = ExistingAncestor(full);
            if (existing is null)
                return new(full, full, 0, 0, false, "No parent folder is available");

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var drive = DriveInfo.GetDrives()
                .Where(d => IsWithin(existing, d.RootDirectory.FullName, comparison))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            if (drive is null || !drive.IsReady)
                return new(full, full, 0, 0, false, "Filesystem is not mounted or ready");
            var requiredMount = RequiredMountPoint(full, comparison);
            if (requiredMount is not null
                && !PathEquals(drive.RootDirectory.FullName, requiredMount, comparison))
                return new(full, full, 0, 0, false,
                    $"Expected network filesystem is not mounted at {requiredMount}");
            return new(drive.Name, full, drive.TotalSize, drive.AvailableFreeSpace, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(path ?? string.Empty, path ?? string.Empty, 0, 0, false, ex.Message);
        }
    }

    private static string? ExistingAncestor(string path)
    {
        var current = path;
        while (!Directory.Exists(current))
        {
            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || parent == current) return null;
            current = parent;
        }
        return current;
    }

    private static bool IsWithin(string path, string root, StringComparison comparison)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, comparison);
    }

    /// <summary>Managed and legacy network roots must be their own mount. An empty mount directory under
    /// the container's root filesystem is not a safe fallback: writing there would silently put media on
    /// the local Docker disk when the NAS is offline.</summary>
    internal static string? RequiredMountPoint(string fullPath, StringComparison comparison)
    {
        var managedRoot = Path.GetFullPath(NetworkMountHelper.MountRoot);
        if (IsWithin(fullPath, managedRoot, comparison))
        {
            var relative = Path.GetRelativePath(managedRoot, fullPath);
            var slug = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrWhiteSpace(slug) ? managedRoot : Path.Combine(managedRoot, slug);
        }

        const string legacyRoot = "/mnt/network-library";
        return IsWithin(fullPath, legacyRoot, comparison) ? legacyRoot : null;
    }

    private static bool PathEquals(string first, string second, StringComparison comparison) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), comparison);
}

public sealed record StorageAdmission(bool Allowed, string Detail, long RequiredBytes,
    IReadOnlyList<StorageReservationRecord> Reservations);

public interface IStorageReservationLease : IAsyncDisposable
{
    StorageAdmission Admission { get; }
}

public interface IStorageSafetyService
{
    Task<IStorageReservationLease> TryReserveAsync(FulfillmentJobDto job, long payloadBytes,
        string destinationRoot, EffectiveLibraryOrganization preferences, CancellationToken ct);
    /// <summary>Reserve one file's worth of temporary space beside an existing library file for an atomic,
    /// lossless container remux. Unlike a download reservation, this does not reserve the work directory.</summary>
    Task<IStorageReservationLease> TryReserveLibraryRewriteAsync(int importedFileId, string title,
        long fileBytes, string destinationRoot, EffectiveLibraryOrganization preferences, CancellationToken ct);
    Task<StorageStatusDto> GetStatusAsync(CancellationToken ct);
    void RecordMaintenance(StorageMaintenanceResult result, int cleanupPendingCount, string? error = null);
}

public sealed record StorageMaintenanceResult(
    IReadOnlyList<StorageCleanupCandidateDto> Remaining,
    int RemovedCount,
    long RemovedBytes);

internal sealed class StorageSafetyService(
    IStorageVolumeProbe volumes,
    IJobStateStore stateStore,
    ILibraryOrganizationProvider libraryPreferences,
    IOptions<StorageOptions> options,
    IOptions<WorkerOptions> worker,
    ILogger<StorageSafetyService> logger) : IStorageSafetyService
{
    private const long GiB = 1024L * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _maintenanceGate = new();
    private readonly Dictionary<int, IReadOnlyList<StorageReservationRecord>> _activeReservations = new();
    private readonly StorageOptions _options = options.Value;
    private readonly WorkerOptions _worker = worker.Value;
    private DateTime _blockedUntil;
    private int? _blockingJobId;
    private string? _blockingTitle;
    private long _blockingRequiredBytes;
    private string? _blockingMessage;
    private StorageMaintenanceResult _maintenance = new([], 0, 0);
    private int _cleanupPendingCount;
    private DateTime? _lastCleanupAt;
    private string? _lastCleanupError;

    public async Task<IStorageReservationLease> TryReserveAsync(FulfillmentJobDto job, long payloadBytes,
        string destinationRoot, EffectiveLibraryOrganization preferences, CancellationToken ct)
    {
        payloadBytes = Math.Max(1, payloadBytes);
        await _gate.WaitAsync(ct);
        try
        {
            var requirements = BuildRequirements(payloadBytes, destinationRoot, preferences);
            var existing = await DurableReservationsAsync(job.Id, ct);
            var minimum = ToBytes(preferences.MinimumFreeSpaceGb);

            foreach (var requirement in requirements)
            {
                var reading = volumes.Read(requirement.Path);
                var alreadyReserved = existing
                    .Where(x => x.VolumeId == requirement.VolumeId)
                    .Sum(x => x.RequiredBytes);
                if (!reading.IsReady)
                    return BlockedLease(job, requirements,
                        $"Storage safety paused '{job.Title}': {requirement.Label} is unavailable ({reading.Error}).");
                if (!HasCapacity(reading.FreeBytes, alreadyReserved, requirement.RequiredBytes, minimum))
                {
                    var needed = minimum + alreadyReserved + requirement.RequiredBytes;
                    return BlockedLease(job, requirements,
                        $"Storage safety paused '{job.Title}': {requirement.Label} needs {FormatBytes(needed)} free " +
                        $"including the {FormatBytes(minimum)} safety floor, but only {FormatBytes(reading.FreeBytes)} is available.");
                }
            }

            _activeReservations[job.Id] = requirements;
            var total = requirements.Sum(x => x.RequiredBytes);
            logger.LogInformation("Reserved {Bytes} across {Volumes} volume(s) for job {JobId} '{Title}'",
                FormatBytes(total), requirements.Count, job.Id, job.Title);
            return new Lease(this, job.Id, new StorageAdmission(true,
                $"Reserved {FormatBytes(total)} for download and import", total, requirements));
        }
        finally { _gate.Release(); }
    }

    public async Task<IStorageReservationLease> TryReserveLibraryRewriteAsync(int importedFileId, string title,
        long fileBytes, string destinationRoot, EffectiveLibraryOrganization preferences, CancellationToken ct)
    {
        fileBytes = Math.Max(1, fileBytes);
        // Job ids are positive. Negative ids give maintenance reservations their own collision-free lane.
        var reservationId = -Math.Max(1, importedFileId);
        await _gate.WaitAsync(ct);
        try
        {
            var reading = volumes.Read(destinationRoot);
            var requirement = new StorageReservationRecord(reading.Id, destinationRoot,
                "Library playback preparation", fileBytes);
            var requirements = new[] { requirement };
            var reserved = (await DurableReservationsAsync(null, ct))
                .Where(item => item.VolumeId == reading.Id)
                .Sum(item => item.RequiredBytes);
            var minimum = ToBytes(preferences.MinimumFreeSpaceGb);
            string? blocked = !reading.IsReady
                ? $"Playback preparation paused for '{title}': the library filesystem is unavailable ({reading.Error})."
                : !HasCapacity(reading.FreeBytes, reserved, fileBytes, minimum)
                    ? $"Playback preparation paused for '{title}': the library needs "
                      + $"{FormatBytes(minimum + reserved + fileBytes)} free for an atomic remux, but only "
                      + $"{FormatBytes(reading.FreeBytes)} is available."
                    : null;
            if (blocked is not null)
            {
                logger.LogWarning("{Detail}", blocked);
                return new Lease(this, reservationId,
                    new StorageAdmission(false, blocked, fileBytes, requirements));
            }

            _activeReservations[reservationId] = requirements;
            return new Lease(this, reservationId, new StorageAdmission(true,
                $"Reserved {FormatBytes(fileBytes)} for atomic playback preparation", fileBytes, requirements));
        }
        finally { _gate.Release(); }
    }

    public async Task<StorageStatusDto> GetStatusAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var preferences = libraryPreferences.Current;
            var reservations = await DurableReservationsAsync(null, ct);
            var paths = new List<(string Label, string Path)> { ("Download workspace", _options.WorkingPath) };
            paths.AddRange(preferences.Destinations.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.RootPath))
                .Select(x => ($"Library: {x.Name}", x.RootPath)));
            if (preferences.Destinations.Count == 0)
            {
                paths.Add(("Movies", preferences.MoviePath));
                paths.Add(("TV", preferences.TvPath));
                paths.Add(("Music", preferences.MusicPath));
            }

            var minimum = ToBytes(preferences.MinimumFreeSpaceGb);
            var snapshots = paths.Where(x => !string.IsNullOrWhiteSpace(x.Path))
                .Select(x => (x.Label, Reading: volumes.Read(x.Path)))
                .GroupBy(x => x.Reading.Id, StringComparer.Ordinal)
                .Select(group =>
                {
                    var first = group.First();
                    return new StorageVolumeStatusDto
                    {
                        Id = first.Reading.Id,
                        Label = string.Join(" / ", group.Select(x => x.Label).Distinct()),
                        Path = first.Reading.Path,
                        TotalBytes = first.Reading.TotalBytes,
                        FreeBytes = first.Reading.FreeBytes,
                        ReservedBytes = reservations.Where(x => x.VolumeId == first.Reading.Id).Sum(x => x.RequiredBytes),
                        MinimumFreeBytes = minimum,
                        IsReady = first.Reading.IsReady,
                        Error = first.Reading.Error
                    };
                }).ToList();

            StorageMaintenanceResult maintenance;
            int cleanupPendingCount;
            DateTime? lastCleanupAt;
            string? lastCleanupError;
            lock (_maintenanceGate)
            {
                maintenance = _maintenance;
                cleanupPendingCount = _cleanupPendingCount;
                lastCleanupAt = _lastCleanupAt;
                lastCleanupError = _lastCleanupError;
            }

            var unavailable = snapshots.FirstOrDefault(x => !x.IsReady);
            var low = snapshots.FirstOrDefault(x => x.IsReady && x.FreeBytes - x.ReservedBytes < minimum * 5 / 4);
            var paused = DateTime.UtcNow < _blockedUntil;
            var maintenanceIssue = cleanupPendingCount > 0 || maintenance.Remaining.Count > 0
                || !string.IsNullOrWhiteSpace(lastCleanupError);
            var state = unavailable is not null ? StorageHealthState.Offline
                : paused ? StorageHealthState.Paused
                : low is not null || maintenanceIssue ? StorageHealthState.Warning
                : StorageHealthState.Healthy;
            var message = unavailable is not null
                ? $"{unavailable.Label} is unavailable: {unavailable.Error}"
                : paused ? _blockingMessage ?? "New downloads are paused by storage safety."
                : low is not null
                    ? $"{low.Label} is approaching its {FormatBytes(minimum)} free-space floor."
                    : !string.IsNullOrWhiteSpace(lastCleanupError)
                        ? $"Storage maintenance could not complete: {lastCleanupError}"
                    : cleanupPendingCount > 0
                        ? $"{cleanupPendingCount} imported transfer(s) still await verified source cleanup."
                    : maintenance.Remaining.Count > 0
                        ? $"{maintenance.Remaining.Count} stale Plex Requests temporary item(s) need cleanup."
                    : "Storage has enough headroom for downloads and imports.";

            return new StorageStatusDto
            {
                WorkerId = _worker.WorkerId,
                ObservedAt = DateTime.UtcNow,
                State = state,
                Message = message,
                BlockingJobId = paused ? _blockingJobId : null,
                BlockingTitle = paused ? _blockingTitle : null,
                BlockingRequiredBytes = paused ? _blockingRequiredBytes : 0,
                CleanupPendingCount = cleanupPendingCount,
                StaleArtifactCount = maintenance.Remaining.Count,
                StaleArtifactBytes = maintenance.Remaining.Sum(x => x.SizeBytes),
                RemovedArtifactCount = maintenance.RemovedCount,
                RemovedArtifactBytes = maintenance.RemovedBytes,
                LastCleanupAt = lastCleanupAt,
                LastCleanupError = lastCleanupError,
                Volumes = snapshots,
                CleanupCandidates = maintenance.Remaining.Take(20).ToList()
            };
        }
        finally { _gate.Release(); }
    }

    public void RecordMaintenance(StorageMaintenanceResult result, int cleanupPendingCount, string? error = null)
    {
        lock (_maintenanceGate)
        {
            _maintenance = result;
            _cleanupPendingCount = Math.Max(0, cleanupPendingCount);
            _lastCleanupAt = DateTime.UtcNow;
            _lastCleanupError = error;
        }
    }

    internal static bool HasCapacity(long freeBytes, long reservedBytes, long requiredBytes, long minimumFreeBytes) =>
        freeBytes - reservedBytes - requiredBytes >= minimumFreeBytes;

    private IReadOnlyList<StorageReservationRecord> BuildRequirements(long payloadBytes, string destinationRoot,
        EffectiveLibraryOrganization preferences)
    {
        var workspace = volumes.Read(_options.WorkingPath);
        var destination = volumes.Read(destinationRoot);
        var temporary = (long)Math.Ceiling(payloadBytes * Math.Clamp(preferences.TemporaryHeadroomPercent, 0, 200) / 100d);
        var byVolume = new Dictionary<string, StorageReservationRecord>(StringComparer.Ordinal);

        Add(new StorageReservationRecord(workspace.Id, _options.WorkingPath, "Download workspace", payloadBytes + temporary));
        if (destination.Id != workspace.Id)
            Add(new StorageReservationRecord(destination.Id, destinationRoot, "Library destination", payloadBytes));
        else if (preferences.TransferMode == TransferMode.Copy)
            Add(new StorageReservationRecord(workspace.Id, _options.WorkingPath, "Download workspace", payloadBytes));
        return byVolume.Values.ToList();

        void Add(StorageReservationRecord value)
        {
            if (byVolume.TryGetValue(value.VolumeId, out var current))
                byVolume[value.VolumeId] = current with { RequiredBytes = current.RequiredBytes + value.RequiredBytes };
            else
                byVolume[value.VolumeId] = value;
        }
    }

    private async Task<List<StorageReservationRecord>> DurableReservationsAsync(int? exceptJobId, CancellationToken ct)
    {
        var byJobAndVolume = new Dictionary<(int JobId, string VolumeId), StorageReservationRecord>();
        foreach (var record in await stateStore.GetAllAsync(ct))
        {
            if (record.Job.Id == exceptJobId) continue;
            foreach (var reservation in record.StorageReservations ?? [])
                byJobAndVolume[(record.Job.Id, reservation.VolumeId)] = reservation;
        }
        foreach (var pair in _activeReservations)
        {
            if (pair.Key == exceptJobId) continue;
            foreach (var reservation in pair.Value)
                byJobAndVolume[(pair.Key, reservation.VolumeId)] = reservation;
        }
        return byJobAndVolume.Values.ToList();
    }

    private IStorageReservationLease BlockedLease(FulfillmentJobDto job,
        IReadOnlyList<StorageReservationRecord> requirements, string detail)
    {
        _blockedUntil = DateTime.UtcNow.AddMinutes(5);
        _blockingJobId = job.Id;
        _blockingTitle = job.Title;
        _blockingRequiredBytes = requirements.Sum(x => x.RequiredBytes);
        _blockingMessage = detail;
        logger.LogWarning("{Detail}", detail);
        return new Lease(this, job.Id, new StorageAdmission(false, detail,
            _blockingRequiredBytes, requirements));
    }

    private async ValueTask ReleaseAsync(int jobId)
    {
        await _gate.WaitAsync();
        try { _activeReservations.Remove(jobId); }
        finally { _gate.Release(); }
    }

    private static long ToBytes(double gib) => (long)(Math.Clamp(gib, 1, 1024) * GiB);

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, (double)bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    private sealed class Lease(StorageSafetyService owner, int jobId, StorageAdmission admission)
        : IStorageReservationLease
    {
        private int _disposed;
        public StorageAdmission Admission { get; } = admission;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && Admission.Allowed)
                await owner.ReleaseAsync(jobId);
        }
    }
}
