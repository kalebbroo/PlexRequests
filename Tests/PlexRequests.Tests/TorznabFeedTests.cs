using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
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

    [Fact]
    public void Music_search_uses_music_capability_and_generic_fallback()
    {
        var urls = TorznabIndexerProvider.BuildQueryUrls(new IndexerConfigDto
        {
            Url = "https://torznab.test/api",
            ApiKey = "secret",
            MediaCapabilities =
            [
                new IndexerMediaCapabilityDto
                    { MediaType = MediaType.Music, Enabled = true, CategoriesCsv = "3000,3040" }
            ]
        }, new FulfillmentJobDto
        {
            MediaType = MediaType.Music,
            Title = "Random Access Memories",
            RequestScope = RequestScopeKind.Album,
            Year = 2013,
            Music = new MusicAcquisitionContextDto
            {
                Kind = MediaKind.Album,
                Artist = "Daft Punk",
                Album = "Random Access Memories"
            }
        }).ToList();

        Assert.Equal(2, urls.Count);
        Assert.Contains("t=music", urls[0]);
        Assert.Contains("cat=3000,3040", urls[0]);
        Assert.Contains("artist=Daft%20Punk", urls[0]);
        Assert.Contains("album=Random%20Access%20Memories", urls[0]);
        Assert.Contains("t=search", urls[1]);
        Assert.Contains("q=Daft%20Punk%20Random%20Access%20Memories", urls[1]);
    }

    [Fact]
    public async Task Feed_categories_preserve_music_type_for_catalog_searches()
    {
        var body = $"""
            <rss xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel>
              <item><title>Daft.Punk-Discovery-FLAC</title>
                <link>{($"magnet:?xt=urn:btih:{FirstHash}").Replace("&", "&amp;")}</link>
                <torznab:attr name="category" value="3000" />
              </item>
            </channel></rss>
            """;
        using var http = new HttpClient(new PagedHandler(body, body));
        var provider = new TorznabIndexerProvider(http,
            new IndexerFetch(NullLogger<IndexerFetch>.Instance),
            NullLogger<TorznabIndexerProvider>.Instance);

        var page = await provider.FetchAsync(new IndexerConfigDto
        {
            Id = 12,
            Name = "Aggregator",
            Implementation = "Torznab",
            Url = "https://torznab.test/api"
        }, null, 100, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(MediaType.Music, item.MediaType);
        Assert.Equal("3000", item.Category);
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
