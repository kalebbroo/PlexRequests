using PlexRequests.Downloader.Import;
using PlexRequests.Downloader.Organize;
using Xunit;

namespace PlexRequests.Tests;

public sealed class TorrentImportCoordinatorTests
{
    [Fact]
    public async Task Concurrent_callers_share_one_successful_physical_import()
    {
        var coordinator = new TorrentImportCoordinator();
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

        var first = coordinator.RunOnceAsync("same-torrent", Import, CancellationToken.None);
        await started.Task;
        var second = coordinator.RunOnceAsync("same-torrent", Import, CancellationToken.None);
        release.TrySetResult();

        var results = await Task.WhenAll(first, second);
        Assert.Equal(1, calls);
        Assert.All(results, result => Assert.True(result.Success));
        Assert.Same(results[0], results[1]);
    }

    [Fact]
    public async Task Failed_import_is_not_cached_and_can_self_heal_on_next_pass()
    {
        var coordinator = new TorrentImportCoordinator();
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

        var first = await coordinator.RunOnceAsync("retryable-torrent", Import, CancellationToken.None);
        var second = await coordinator.RunOnceAsync("retryable-torrent", Import, CancellationToken.None);

        Assert.False(first.Success);
        Assert.True(second.Success);
        Assert.Equal(2, calls);
    }
}
