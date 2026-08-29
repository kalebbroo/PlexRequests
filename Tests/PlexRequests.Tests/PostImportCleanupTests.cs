using Microsoft.Extensions.Logging.Abstractions;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Download;
using PlexRequests.Downloader.Organize;
using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

public sealed class PostImportCleanupTests
{
    [Theory]
    [InlineData(TransferMode.Move, false, true)]
    [InlineData(TransferMode.Move, true, true)]
    [InlineData(TransferMode.Copy, true, true)]
    [InlineData(TransferMode.Copy, false, false)]
    [InlineData(TransferMode.Hardlink, false, false)]
    [InlineData(TransferMode.Hardlink, true, false)]
    public void Torrent_payload_cleanup_honors_library_transfer_policy(
        TransferMode mode, bool deleteSourceAfterImport, bool expected)
    {
        var preferences = new EffectiveLibraryOrganization
        {
            TransferMode = mode,
            DeleteSourceAfterImport = deleteSourceAfterImport
        };
        var torrentCapabilities = new AcquisitionBackendCapabilities(true, true, true);

        Assert.Equal(expected, PostImportCleanup.ShouldRemoveSourceData(preferences, torrentCapabilities));
    }

    [Fact]
    public async Task Move_import_removes_backend_payload_after_destination_verification()
    {
        var destination = NewTempFile("verified-media");
        try
        {
            var backend = new RecordingBackend(canKeepSourceData: true);
            var cleanup = Create(backend, TransferMode.Move);
            var result = SuccessfulResult(destination);

            Assert.True(await cleanup.RunAsync(AcquisitionProtocol.Torrent, "torrent-id", result, CancellationToken.None));
            Assert.Equal(new[] { true }, backend.RemoveDataCalls);
        }
        finally { File.Delete(destination); }
    }

    [Fact]
    public async Task Concurrent_callers_share_one_successful_backend_removal()
    {
        var destination = NewTempFile("verified-media");
        try
        {
            var backend = new RecordingBackend(canKeepSourceData: true, removalDelay: TimeSpan.FromMilliseconds(50));
            var cleanup = Create(backend, TransferMode.Move);
            var result = SuccessfulResult(destination);

            var first = cleanup.RunAsync(AcquisitionProtocol.Torrent, "shared-id", result, CancellationToken.None);
            var second = cleanup.RunAsync(AcquisitionProtocol.Torrent, "shared-id", result, CancellationToken.None);

            Assert.True(await first);
            Assert.True(await second);
            Assert.Equal(1, backend.RemoveCallCount);
            Assert.Equal(new[] { true }, backend.RemoveDataCalls);
        }
        finally { File.Delete(destination); }
    }

    [Fact]
    public async Task Declined_cleanup_is_not_cached_and_can_be_retried()
    {
        var destination = NewTempFile("verified-media");
        try
        {
            var backend = new RecordingBackend(canKeepSourceData: true, removalResults: new[] { false, true });
            var cleanup = Create(backend, TransferMode.Move);
            var result = SuccessfulResult(destination);

            Assert.False(await cleanup.RunAsync(
                AcquisitionProtocol.Torrent, "retry-id", result, CancellationToken.None));
            Assert.True(await cleanup.RunAsync(
                AcquisitionProtocol.Torrent, "retry-id", result, CancellationToken.None));
            Assert.Equal(2, backend.RemoveCallCount);
        }
        finally { File.Delete(destination); }
    }

    [Fact]
    public async Task Copy_import_keeps_payload_when_delete_source_is_disabled()
    {
        var destination = NewTempFile("verified-media");
        try
        {
            var backend = new RecordingBackend(canKeepSourceData: true);
            var cleanup = Create(backend, TransferMode.Copy, deleteSourceAfterImport: false);

            Assert.True(await cleanup.RunAsync(AcquisitionProtocol.Torrent, "torrent-id",
                SuccessfulResult(destination), CancellationToken.None));
            Assert.Equal(new[] { false }, backend.RemoveDataCalls);
        }
        finally { File.Delete(destination); }
    }

    [Fact]
    public async Task Failed_import_never_removes_the_transfer()
    {
        var backend = new RecordingBackend(canKeepSourceData: true);
        var cleanup = Create(backend, TransferMode.Move);

        Assert.False(await cleanup.RunAsync(AcquisitionProtocol.Torrent, "torrent-id",
            ImportResult.Fail("copy failed"), CancellationToken.None));
        Assert.Empty(backend.RemoveDataCalls);
    }

    [Fact]
    public async Task Missing_or_size_mismatched_destination_never_removes_the_transfer()
    {
        var backend = new RecordingBackend(canKeepSourceData: true);
        var cleanup = Create(backend, TransferMode.Move);
        var missing = Path.Combine(Path.GetTempPath(), $"plexrequests-missing-{Guid.NewGuid():N}.mkv");

        var missingResult = ImportResult.Ok(new[]
        {
            new ImportedFileRecord("source", missing, "video", 1, 1, 100)
        });
        Assert.False(await cleanup.RunAsync(AcquisitionProtocol.Torrent, "missing", missingResult, CancellationToken.None));

        var destination = NewTempFile("short");
        try
        {
            var mismatchResult = ImportResult.Ok(new[]
            {
                new ImportedFileRecord("source", destination, "video", 1, 1, 999)
            });
            Assert.False(await cleanup.RunAsync(AcquisitionProtocol.Torrent, "mismatch", mismatchResult, CancellationToken.None));
        }
        finally { File.Delete(destination); }

        Assert.Empty(backend.RemoveDataCalls);
    }

    [Fact]
    public async Task Invalid_destination_path_is_retained_without_touching_the_backend()
    {
        var backend = new RecordingBackend(canKeepSourceData: true);
        var cleanup = Create(backend, TransferMode.Move);
        var result = ImportResult.Ok(new[]
        {
            new ImportedFileRecord("source", "invalid\0destination", "video", 1, 1, 100)
        });

        Assert.False(await cleanup.RunAsync(AcquisitionProtocol.Torrent, "unsafe", result, CancellationToken.None));
        Assert.Empty(backend.RemoveDataCalls);
    }

    private static PostImportCleanup Create(
        RecordingBackend backend, TransferMode mode, bool deleteSourceAfterImport = false)
    {
        var registry = new AcquisitionBackendRegistry(new[] { backend });
        var preferences = new StubLibraryPreferences(new EffectiveLibraryOrganization
        {
            TransferMode = mode,
            DeleteSourceAfterImport = deleteSourceAfterImport
        });
        return new PostImportCleanup(registry, preferences, NullLogger<PostImportCleanup>.Instance);
    }

    private static ImportResult SuccessfulResult(string destination)
    {
        var length = new FileInfo(destination).Length;
        return ImportResult.Ok(new[]
        {
            new ImportedFileRecord("source", destination, "video", 1, 1, length)
        });
    }

    private static string NewTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plexrequests-cleanup-{Guid.NewGuid():N}.mkv");
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class StubLibraryPreferences(EffectiveLibraryOrganization current)
        : ILibraryOrganizationProvider
    {
        public EffectiveLibraryOrganization Current { get; } = current;
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingBackend(
        bool canKeepSourceData,
        TimeSpan? removalDelay = null,
        IReadOnlyList<bool>? removalResults = null) : IAcquisitionBackend
    {
        private int _removeCallCount;
        private readonly Queue<bool> _removalResults = new(removalResults ?? new[] { true });
        public List<bool> RemoveDataCalls { get; } = new();
        public int RemoveCallCount => Volatile.Read(ref _removeCallCount);
        public AcquisitionProtocol Protocol => AcquisitionProtocol.Torrent;
        public AcquisitionBackendCapabilities Capabilities { get; } = new(false, false, canKeepSourceData);
        public Task<string?> EnqueueAsync(AcquisitionRequest request, CancellationToken ct) => Task.FromResult<string?>("id");
        public Task<TransferStatus?> GetStatusAsync(string transferId, CancellationToken ct) => Task.FromResult<TransferStatus?>(null);
        public TransferHealthDecision EvaluateHealth(TransferStatus? status, DateTime addedAt,
            DateTime? progressChangedAt, DateTime now) => new(TransferVerdict.Wait);
        public async Task<bool> RemoveAsync(string transferId, bool removeData, CancellationToken ct)
        {
            Interlocked.Increment(ref _removeCallCount);
            RemoveDataCalls.Add(removeData);
            if (removalDelay is not null) await Task.Delay(removalDelay.Value, ct);
            return _removalResults.Count > 0 ? _removalResults.Dequeue() : true;
        }
        public Task<bool> SetWantedFilesAsync(string transferId, IReadOnlyList<bool> keep, CancellationToken ct) =>
            Task.FromResult(false);
    }
}
