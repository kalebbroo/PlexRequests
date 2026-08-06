using System.Collections.Concurrent;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Indexers;

public interface IIndexerRateLimiter
{
    /// <summary>Block until this indexer is allowed another request.</summary>
    Task WaitAsync(IndexerConfigDto indexer, CancellationToken ct);
}

/// <summary>
/// A per-indexer request throttle. Previously there was none at all: the client fanned every provider out
/// with an unbounded <c>Task.WhenAll</c>, and two scrapers each fired up to 25 detail-page fetches in
/// parallel at a single host — a good way to get an IP rate-limited or blocked outright.
///
/// Deliberately a simple fixed-window counter rather than a token bucket: indexer limits are advisory and
/// coarse (tens per minute), the numbers are small, and this keeps the state to one timestamp queue per
/// indexer with no background timer to leak.
/// </summary>
public class IndexerRateLimiter(ILogger<IndexerRateLimiter> logger) : IIndexerRateLimiter
{
    private sealed class Window
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly Queue<DateTime> Recent = new();
    }

    private readonly ConcurrentDictionary<int, Window> _windows = new();

    public async Task WaitAsync(IndexerConfigDto indexer, CancellationToken ct)
    {
        var limit = indexer.RateLimitPerMinute;
        if (limit <= 0) return;

        var window = _windows.GetOrAdd(indexer.Id, _ => new Window());
        await window.Gate.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            var cutoff = now.AddMinutes(-1);
            while (window.Recent.Count > 0 && window.Recent.Peek() < cutoff) window.Recent.Dequeue();

            if (window.Recent.Count >= limit)
            {
                var wait = window.Recent.Peek().AddMinutes(1) - now;
                if (wait > TimeSpan.Zero)
                {
                    logger.LogDebug("Rate limit reached for {Indexer} ({Limit}/min); waiting {Ms}ms",
                        indexer.Name, limit, (int)wait.TotalMilliseconds);
                    await Task.Delay(wait, ct);
                    var after = DateTime.UtcNow.AddMinutes(-1);
                    while (window.Recent.Count > 0 && window.Recent.Peek() < after) window.Recent.Dequeue();
                }
            }
            window.Recent.Enqueue(DateTime.UtcNow);
        }
        finally { window.Gate.Release(); }
    }
}
