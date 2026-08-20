using PlexRequests.Downloader.Import;
using PlexRequests.Downloader.Organize;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class TransferImportCoordinatorTests
{
    [Fact]
    public async Task Concurrent_callers_share_one_successful_physical_import()
    {
        var coordinator = new TransferImportCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<ImportResult> Import(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            return ImportResult.Ok(new[]
            {
                new ImportedFileRecord("source", "destination", "video", 9, 1, 100)
            });
        }

        var first = coordinator.RunOnceAsync(AcquisitionProtocol.Torrent, "same-torrent", Import, CancellationToken.None);
        await started.Task;
        var second = coordinator.RunOnceAsync(AcquisitionProtocol.Torrent, "same-torrent", Import, CancellationToken.None);
        release.TrySetResult();

        var results = await Task.WhenAll(first, second);
        Assert.Equal(1, calls);
        Assert.All(results, result => Assert.True(result.Success));
        Assert.Same(results[0], results[1]);
    }

    [Fact]
    public async Task Failed_import_is_not_cached_and_can_self_heal_on_next_pass()
    {
        var coordinator = new TransferImportCoordinator();
        var calls = 0;

        Task<ImportResult> Import(CancellationToken _)
        {
            var attempt = Interlocked.Increment(ref calls);
            return Task.FromResult(attempt == 1
                ? ImportResult.Fail("transient NAS failure")
                : ImportResult.Ok(new[]
                {
                    new ImportedFileRecord("source", "destination", "video", 9, 1, 100)
                }));
        }

        var first = await coordinator.RunOnceAsync(AcquisitionProtocol.Torrent, "retryable-torrent", Import, CancellationToken.None);
        var second = await coordinator.RunOnceAsync(AcquisitionProtocol.Torrent, "retryable-torrent", Import, CancellationToken.None);

        Assert.False(first.Success);
        Assert.True(second.Success);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Same_backend_id_on_different_protocols_does_not_share_an_import()
    {
        var coordinator = new TransferImportCoordinator();
        var calls = 0;
        Task<ImportResult> Import(CancellationToken _) => Task.FromResult(
            ImportResult.Ok(new[]
            {
                new ImportedFileRecord("source", $"destination-{Interlocked.Increment(ref calls)}", "audio", null, null, 100)
            }));

        await coordinator.RunOnceAsync(AcquisitionProtocol.Torrent, "same-id", Import, CancellationToken.None);
        await coordinator.RunOnceAsync(AcquisitionProtocol.DirectAudio, "same-id", Import, CancellationToken.None);

        Assert.Equal(2, calls);
    }
}
