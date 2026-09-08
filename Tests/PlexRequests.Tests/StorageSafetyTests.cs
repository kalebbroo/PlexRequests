using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Download;
using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

public sealed class StorageSafetyTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void Admission_keeps_the_configured_floor_after_other_reservations_and_new_work()
    {
        Assert.True(StorageSafetyService.HasCapacity(
            freeBytes: 100, reservedBytes: 20, requiredBytes: 50, minimumFreeBytes: 30));
        Assert.False(StorageSafetyService.HasCapacity(
            freeBytes: 99, reservedBytes: 20, requiredBytes: 50, minimumFreeBytes: 30));
    }

    [Theory]
    [InlineData("/mnt/nas/anime/TV", "/mnt/nas/anime")]
    [InlineData("/mnt/network-library/Movies", "/mnt/network-library")]
    [InlineData("/data/library/Movies", null)]
    public void Network_destinations_require_their_expected_mount_instead_of_falling_back_to_root_disk(
        string path, string? expected)
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.Equal(expected, PhysicalStorageVolumeProbe.RequiredMountPoint(path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Admission_counts_durable_reservations_before_allowing_another_download()
    {
        var previous = new ActiveJobRecord(
            new FulfillmentJobDto { Id = 1, Title = "Existing" },
            [new TransferItem("existing", null, null, false)],
            StorageReservations: [new StorageReservationRecord("disk", "/data", "Download workspace", 45 * GiB)]);
        var preferences = new EffectiveLibraryOrganization
        {
            TransferMode = TransferMode.Copy,
            MinimumFreeSpaceGb = 10,
            TemporaryHeadroomPercent = 50
        };
        var service = new StorageSafetyService(
            new FixedVolumeProbe(100 * GiB),
            new FixedStateStore([previous]),
            new FixedLibraryPreferences(preferences),
            Options.Create(new StorageOptions { WorkingPath = "/data" }),
            Options.Create(new WorkerOptions { WorkerId = "test" }),
            NullLogger<StorageSafetyService>.Instance);

        await using var lease = await service.TryReserveAsync(
            new FulfillmentJobDto { Id = 2, Title = "New" }, 20 * GiB,
            "/data/library", preferences, CancellationToken.None);

        Assert.False(lease.Admission.Allowed);
        Assert.Equal(50 * GiB, lease.Admission.RequiredBytes);
        Assert.Contains("needs", lease.Admission.Detail);
    }

    [Fact]
    public async Task Incomplete_durable_cleanup_is_visible_as_a_storage_warning()
    {
        var preferences = new EffectiveLibraryOrganization { MinimumFreeSpaceGb = 10 };
        var service = new StorageSafetyService(
            new FixedVolumeProbe(100 * GiB),
            new FixedStateStore([]),
            new FixedLibraryPreferences(preferences),
            Options.Create(new StorageOptions { WorkingPath = "/data" }),
            Options.Create(new WorkerOptions { WorkerId = "test" }),
            NullLogger<StorageSafetyService>.Instance);
        service.RecordMaintenance(new StorageMaintenanceResult([], 0, 0), cleanupPendingCount: 2);

        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal(StorageHealthState.Warning, status.State);
        Assert.Equal(2, status.CleanupPendingCount);
        Assert.Contains("await verified source cleanup", status.Message);
    }

    [Fact]
    public async Task LegacyRemuxReservationUsesOnlyTheLibraryVolumeAndKeepsTheFreeSpaceFloor()
    {
        var preferences = new EffectiveLibraryOrganization { MinimumFreeSpaceGb = 20 };
        var service = new StorageSafetyService(
            new FixedVolumeProbe(40 * GiB),
            new FixedStateStore([]),
            new FixedLibraryPreferences(preferences),
            Options.Create(new StorageOptions { WorkingPath = "/small-local-disk" }),
            Options.Create(new WorkerOptions { WorkerId = "test" }),
            NullLogger<StorageSafetyService>.Instance);

        await using var lease = await service.TryReserveLibraryRewriteAsync(
            12, "Legacy show", 25 * GiB, "/large-library", preferences, CancellationToken.None);

        Assert.False(lease.Admission.Allowed);
        Assert.Equal(25 * GiB, lease.Admission.RequiredBytes);
        Assert.Single(lease.Admission.Reservations);
        Assert.Equal("/large-library", lease.Admission.Reservations[0].Path);
    }

    [Fact]
    public void Manifest_estimate_counts_only_files_selected_during_preflight()
    {
        var prepared = Prepared(
            candidateBytes: 9_000,
            manifest: new AcquisitionManifest([
                new("wanted-1.mkv", 100),
                new("sample.mkv", 8_000),
                new("wanted-2.mkv", 200)
            ]),
            wanted: [true, false, true]);

        Assert.Equal(300, FulfillmentPipeline.EstimatedPayloadBytes(
            prepared, MediaType.TvShow, new StorageOptions()));
    }

    [Theory]
    [InlineData(MediaType.Movie, 8)]
    [InlineData(MediaType.Music, 2)]
    public void Unknown_release_size_uses_a_conservative_media_fallback(MediaType mediaType, long expectedGb)
    {
        var prepared = Prepared(candidateBytes: 0, sizeKnown: false);

        Assert.Equal(expectedGb * 1024 * 1024 * 1024,
            FulfillmentPipeline.EstimatedPayloadBytes(prepared, mediaType, new StorageOptions()));
    }

    [Fact]
    public void Cleaner_removes_only_old_inactive_artifacts_owned_by_plex_requests()
    {
        var root = Path.Combine(Path.GetTempPath(), $"plexrequests-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var stagingRoot = Path.Combine(root, ".plexrequests-staging");
            var abandoned = Path.Combine(stagingRoot, "42-old-transfer");
            var active = Path.Combine(stagingRoot, "7-live-transfer");
            Directory.CreateDirectory(abandoned);
            Directory.CreateDirectory(active);
            var abandonedFile = Path.Combine(abandoned, "episode.mkv");
            var activeFile = Path.Combine(active, "episode.mkv");
            File.WriteAllText(abandonedFile, "abandoned");
            File.WriteAllText(activeFile, "active");

            var ownedPartial = Path.Combine(root, ".episode.mkv.plexrequests-test.partial");
            var ownedRemux = Path.Combine(root, ".episode.plexrequests-remux-test.mkv");
            var unrelatedPartial = Path.Combine(root, "another-app.partial");
            var ordinaryMedia = Path.Combine(root, "episode.mkv");
            File.WriteAllText(ownedPartial, "partial");
            File.WriteAllText(ownedRemux, "remux");
            File.WriteAllText(unrelatedPartial, "keep");
            File.WriteAllText(ordinaryMedia, "keep");

            var old = DateTime.UtcNow.AddHours(-12);
            File.SetLastWriteTimeUtc(abandonedFile, old);
            Directory.SetLastWriteTimeUtc(abandoned, old);
            File.SetLastWriteTimeUtc(activeFile, old);
            Directory.SetLastWriteTimeUtc(active, old);
            File.SetLastWriteTimeUtc(ownedPartial, old);
            File.SetLastWriteTimeUtc(ownedRemux, old);
            File.SetLastWriteTimeUtc(unrelatedPartial, old);
            File.SetLastWriteTimeUtc(ordinaryMedia, old);

            var cleaner = new StorageArtifactCleaner(NullLogger<StorageArtifactCleaner>.Instance);
            var result = cleaner.Sweep([root], new HashSet<int> { 7 }, TimeSpan.FromHours(6), remove: true);

            Assert.Equal(3, result.RemovedCount);
            Assert.True(result.RemovedBytes > 0);
            Assert.Empty(result.Remaining);
            Assert.False(Directory.Exists(abandoned));
            Assert.False(File.Exists(ownedPartial));
            Assert.False(File.Exists(ownedRemux));
            Assert.True(Directory.Exists(active));
            Assert.True(File.Exists(unrelatedPartial));
            Assert.True(File.Exists(ordinaryMedia));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cleaner_reports_owned_artifacts_when_automatic_removal_is_disabled()
    {
        var root = Path.Combine(Path.GetTempPath(), $"plexrequests-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, ".movie.mkv.plexrequests-test.partial");
            File.WriteAllText(path, "partial");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-12));
            var cleaner = new StorageArtifactCleaner(NullLogger<StorageArtifactCleaner>.Instance);

            var result = cleaner.Sweep([root], new HashSet<int>(), TimeSpan.FromHours(6), remove: false);

            var candidate = Assert.Single(result.Remaining);
            Assert.Equal("Partial file", candidate.Kind);
            Assert.Equal(path, candidate.Path);
            Assert.True(File.Exists(path));
            Assert.Equal(0, result.RemovedCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Telemetry_alerts_once_per_incident_and_alerts_again_after_recovery()
    {
        var store = new StorageTelemetryStore();
        var warning = new StorageStatusDto
        {
            State = StorageHealthState.Warning,
            Message = "Download workspace is low"
        };

        Assert.True(store.Update(warning));
        Assert.False(store.Update(warning));
        Assert.False(store.Update(new StorageStatusDto
        {
            State = StorageHealthState.Healthy,
            Message = "Storage is healthy"
        }));
        Assert.True(store.Update(warning));
        Assert.Same(warning, store.Get());
    }

    private static PreparedDownloadItem Prepared(long candidateBytes, bool sizeKnown = true,
        AcquisitionManifest? manifest = null, IReadOnlyList<bool>? wanted = null)
    {
        var candidate = new ReleaseCandidate
        {
            ReleaseName = "Release",
            Acquisition = AcquisitionResource.Torrent("magnet:?xt=urn:btih:abc", "abc"),
            SizeBytes = candidateBytes,
            SizeKnown = sizeKnown
        };
        return new PreparedDownloadItem(
            new DownloadPlanItem(candidate, 1, null, true), null!, manifest, wanted, null);
    }

    private sealed class FixedVolumeProbe(long freeBytes) : IStorageVolumeProbe
    {
        public StorageVolumeReading Read(string path) =>
            new("disk", path, 200 * GiB, freeBytes, true);
    }

    private sealed class FixedStateStore(IReadOnlyList<ActiveJobRecord> records) : IJobStateStore
    {
        public Task SaveAsync(ActiveJobRecord record, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveAsync(int jobId, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ActiveJobRecord>> GetAllAsync(CancellationToken ct) => Task.FromResult(records);
    }

    private sealed class FixedLibraryPreferences(EffectiveLibraryOrganization current)
        : ILibraryOrganizationProvider
    {
        public EffectiveLibraryOrganization Current { get; } = current;
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
