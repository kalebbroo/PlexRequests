using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Shared.DTOs;
using Xunit;

namespace PlexRequests.Tests;

public sealed class TorznabFeedTests
{
    private const string FirstHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string SecondHash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public async Task Torrent_only_rows_do_not_stop_feed_pagination_early()
    {
        var firstPage = new StringBuilder("<rss><channel>")
            .Append(Item("First.Release.S01E01", $"magnet:?xt=urn:btih:{FirstHash}"));
        for (var i = 1; i < 100; i++)
            firstPage.Append(Item($"Torrent.Only.{i}", $"https://example.test/{i}.torrent"));
        firstPage.Append("</channel></rss>");

        var handler = new PagedHandler(
            firstPage.ToString(),
            $"<rss><channel>{Item("Second.Release.S01E02", $"magnet:?xt=urn:btih:{SecondHash}")}</channel></rss>");
        using var http = new HttpClient(handler);
        var provider = new TorznabIndexerProvider(
            http,
            new IndexerFetch(NullLogger<IndexerFetch>.Instance),
            NullLogger<TorznabIndexerProvider>.Instance);

        var page = await provider.FetchAsync(new IndexerConfigDto
        {
            Id = 12,
            Name = "Aggregator",
            Implementation = "Torznab",
            Url = "https://torznab.test/api"
        }, "OLDER-CURSOR", 100, CancellationToken.None);

        Assert.Equal(2, handler.Calls);
        Assert.Equal(new[] { FirstHash.ToLowerInvariant(), SecondHash.ToLowerInvariant() },
            page.Items.Select(x => x.ExternalId));
    }

    private static string Item(string title, string link) =>
        $"<item><title>{title}</title><link>{link.Replace("&", "&amp;")}</link></item>";

    private sealed class PagedHandler(string firstPage, string secondPage) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            var body = request.RequestUri?.Query.Contains("offset=100", StringComparison.Ordinal) == true
                ? secondPage : firstPage;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }
}
