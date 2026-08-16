using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Shared.DTOs;
using Xunit;

namespace PlexRequests.Tests;

public sealed class NyaaFeedTests
{
    private const string NewHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string OldHash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public async Task Incremental_feed_stops_at_the_committed_cursor_and_preserves_metadata()
    {
        var xml = $$"""
            <rss xmlns:nyaa="https://nyaa.si/xmlns/nyaa"><channel>
              <item>
                <title>Example.Show.S01E02.1080p.WEB-DL</title>
                <link>https://nyaa.si/view/2</link>
                <pubDate>Sat, 15 Aug 2026 15:00:00 +0000</pubDate>
                <nyaa:infoHash>{{NewHash}}</nyaa:infoHash>
                <nyaa:seeders>25</nyaa:seeders><nyaa:leechers>3</nyaa:leechers><nyaa:size>1.5 GiB</nyaa:size>
              </item>
              <item>
                <title>Example.Show.S01E01.1080p.WEB-DL</title>
                <link>https://nyaa.si/view/1</link>
                <pubDate>Fri, 14 Aug 2026 15:00:00 +0000</pubDate>
                <nyaa:infoHash>{{OldHash}}</nyaa:infoHash>
                <nyaa:seeders>50</nyaa:seeders><nyaa:leechers>1</nyaa:leechers><nyaa:size>1 GiB</nyaa:size>
              </item>
            </channel></rss>
            """;
        using var http = new HttpClient(new StaticHandler(xml)) { BaseAddress = new Uri("https://nyaa.si") };
        var provider = new NyaaIndexerProvider(
            http,
            new IndexerFetch(NullLogger<IndexerFetch>.Instance),
            Options.Create(new IndexerOptions()),
            NullLogger<NyaaIndexerProvider>.Instance);

        var page = await provider.FetchAsync(
            new IndexerConfigDto { Id = 9, Name = "Nyaa", Implementation = "Nyaa" },
            OldHash,
            100,
            CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(NewHash, page.NextCursor);
        Assert.Equal(NewHash, item.ExternalId);
        Assert.Equal(25, item.Seeders);
        Assert.Equal(3, item.Leechers);
        Assert.Equal(1_610_612_736, item.SizeBytes);
        Assert.Equal(new DateTime(2026, 8, 15, 15, 0, 0, DateTimeKind.Utc), item.PublishedAt);
        Assert.StartsWith("magnet:?xt=urn:btih:", item.MagnetUri);
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
    }
}
