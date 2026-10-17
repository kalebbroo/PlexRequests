using System.Net.Http;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class ExtToIndexerProviderTests
{
    // Shape of an ext.to /browse/ results page, from a live capture: each name link carries
    // torrent-title-link, the comment link shares the detail path with a #comments fragment, and the
    // magnet button has no real href (the magnet is fetched after a click).
    private const string SearchPage = """
        <html><body>
        <nav><a href="/browse/">Browse</a><a href="/advanced/">Advanced Search</a></nav>
        <table class="table table-striped table-hover search-table"><tbody>
        <tr><td class="text-left">
          <a class="torrent-title-link" href="/marvels-spidey-and-his-amazing-friends-season-3-19052611/">Marvels Spidey</a>
          <a class="dwn-btn comments-torrents-btn" href="/marvels-spidey-and-his-amazing-friends-season-3-19052611/#comments">9</a>
          <a class="dwn-btn search-magnet-btn" href="javascript:void(0);" data-id="19052611"></a>
        </td></tr>
        <tr><td class="text-left">
          <a class="torrent-title-link" href="/spidey-and-his-amazing-friends-s03e01-14862277/">Spidey S03E01</a>
          <a class="dwn-btn search-magnet-btn" href="javascript:void(0);" data-id="14862277"></a>
        </td></tr>
        </tbody></table>
        </body></html>
        """;

    [Fact]
    public void Detail_paths_are_release_slugs_and_skip_comment_and_navigation_links()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(SearchPage);

        var paths = ExtToIndexerProvider.DetailPaths(doc);

        Assert.Equal(new[]
        {
            "/marvels-spidey-and-his-amazing-friends-season-3-19052611/",
            "/spidey-and-his-amazing-friends-s03e01-14862277/"
        }, paths);
    }

    [Fact]
    public async Task Blocked_detail_page_surfaces_as_a_block_instead_of_no_results()
    {
        var provider = new ExtToIndexerProvider(
            new HttpClient(),
            Options.Create(new IndexerOptions
            {
                ExtToEnabled = true,
                ExtToSearchPath = "/browse/?q={query}&sort=seeders&order=desc"
            }),
            new SearchThenBlockFetch(SearchPage),
            NullLogger<ExtToIndexerProvider>.Instance);
        var indexer = new IndexerConfigDto { Id = 1, Name = "ext.to", Implementation = "ext.to", MaxDetailFetches = 8 };
        var job = new FulfillmentJobDto { Title = "Spidey and His Amazing Friends", MediaType = MediaType.TvShow };

        var ex = await Assert.ThrowsAsync<IndexerBlockedException>(() => provider.SearchAsync(indexer, job, CancellationToken.None));

        Assert.Equal(IndexerBlockReason.CloudflareChallenge, ex.Reason);
    }

    /// <summary>Serves the search page, and refuses every detail page the way Cloudflare does.</summary>
    private sealed class SearchThenBlockFetch(string searchHtml) : IIndexerFetch
    {
        public Task<string> GetStringAsync(HttpClient http, IndexerConfigDto indexer, string url, CancellationToken ct)
        {
            if (url.StartsWith("/browse/", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(searchHtml);
            throw new IndexerBlockedException(IndexerBlockReason.CloudflareChallenge, "detail page refused");
        }
    }
}
