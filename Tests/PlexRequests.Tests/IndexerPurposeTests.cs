using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Shared.DTOs;
using Xunit;

namespace PlexRequests.Tests;

public sealed class IndexerPurposeTests
{
    [Fact]
    public void Interactive_search_uses_its_own_toggle_instead_of_the_automatic_toggle()
    {
        var indexer = new IndexerConfigDto
        {
            Enabled = true,
            EnableAutomaticSearch = false,
            EnableInteractiveSearch = true,
            EnableIngestion = false
        };

        Assert.True(IndexerClient.IsEnabledFor(indexer, IndexerSearchPurpose.Interactive, DateTime.UtcNow));
        Assert.False(IndexerClient.IsEnabledFor(indexer, IndexerSearchPurpose.Automatic, DateTime.UtcNow));
        Assert.False(IndexerClient.IsEnabledFor(indexer, IndexerSearchPurpose.Monitoring, DateTime.UtcNow));
    }

    [Fact]
    public void An_open_durable_circuit_skips_every_ordinary_search_until_its_probe_time()
    {
        var now = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
        var indexer = new IndexerConfigDto
        {
            Enabled = true,
            EnableAutomaticSearch = true,
            EnableInteractiveSearch = true,
            EnableIngestion = true,
            SearchCircuitOpenUntil = now.AddHours(1)
        };

        Assert.False(IndexerClient.IsEnabledFor(indexer, IndexerSearchPurpose.Automatic, now));
        Assert.True(IndexerClient.IsEnabledFor(indexer, IndexerSearchPurpose.Automatic, now.AddHours(1)));
    }
}
