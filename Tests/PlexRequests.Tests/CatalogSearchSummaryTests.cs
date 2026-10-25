using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

/// <summary>The defer reason shown on a replacement must say what the catalog contributed, not just what indexers did.</summary>
public sealed class CatalogSearchSummaryTests
{
    [Fact]
    public void A_catalog_that_was_switched_off_is_named_in_the_summary_with_its_reason()
    {
        var result = new IndexerSearchResult(Array.Empty<ReleaseCandidate>(),
        [
            new ProviderSearchOutcome(0, IndexerSearchResult.CatalogProvider, false, 0,
                "UseCatalogForSearch is off in download preferences", 0),
            new ProviderSearchOutcome(3, "ext.to", false, 0, null, 120, IndexerBlockReason.CloudflareChallenge)
        ]);

        Assert.Equal("Catalog: UseCatalogForSearch is off in download preferences, ext.to: CloudflareChallenge",
            result.Summary);
    }

    [Fact]
    public void A_catalog_that_ran_out_of_budget_is_reported_as_timed_out_not_as_empty()
    {
        var result = new IndexerSearchResult(Array.Empty<ReleaseCandidate>(),
        [
            new ProviderSearchOutcome(0, IndexerSearchResult.CatalogProvider, false, 0, "timed out after 2s", 2001)
        ]);

        Assert.Equal("Catalog: timed out after 2s", result.Summary);
    }

    [Fact]
    public void A_catalog_that_answered_reports_its_candidate_count_like_any_provider()
    {
        var result = new IndexerSearchResult(Array.Empty<ReleaseCandidate>(),
        [
            new ProviderSearchOutcome(0, IndexerSearchResult.CatalogProvider, true, 4, null, 15)
        ]);

        Assert.Equal("Catalog: 4", result.Summary);
    }
}
