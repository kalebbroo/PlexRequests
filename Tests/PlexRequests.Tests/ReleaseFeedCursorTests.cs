using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Shared.DTOs;
using Xunit;

namespace PlexRequests.Tests;

public sealed class ReleaseFeedCursorTests
{
    [Fact]
    public void A_backlog_larger_than_one_batch_is_walked_oldest_first_without_gaps()
    {
        var newestFirst = new[] { "A", "B", "C", "D", "E" }
            .Select(id => new CatalogItemDto { ExternalId = id, ReleaseName = id })
            .ToList();

        var first = ReleaseFeedCursor.Select(newestFirst, "E", 2);
        Assert.Equal(new[] { "C", "D" }, first.Items.Select(x => x.ExternalId));
        Assert.Equal("C", first.NextCursor);

        var second = ReleaseFeedCursor.Select(newestFirst, first.NextCursor, 2);
        Assert.Equal(new[] { "A", "B" }, second.Items.Select(x => x.ExternalId));
        Assert.Equal("A", second.NextCursor);
    }

    [Fact]
    public void First_run_prefers_the_newest_bounded_slice_instead_of_backfilling_forever()
    {
        var newestFirst = new[] { "A", "B", "C" }
            .Select(id => new CatalogItemDto { ExternalId = id, ReleaseName = id })
            .ToList();

        var page = ReleaseFeedCursor.Select(newestFirst, null, 2);
        Assert.Equal(new[] { "A", "B" }, page.Items.Select(x => x.ExternalId));
        Assert.Equal("A", page.NextCursor);
    }

    [Fact]
    public void Duplicate_aggregator_rows_are_collapsed_before_batching()
    {
        var newestFirst = new[] { "A", "a", "B", "C" }
            .Select(id => new CatalogItemDto { ExternalId = id, ReleaseName = id })
            .ToList();

        var page = ReleaseFeedCursor.Select(newestFirst, "C", 10);

        Assert.Equal(new[] { "A", "B" }, page.Items.Select(x => x.ExternalId));
        Assert.Equal("A", page.NextCursor);
    }
}
