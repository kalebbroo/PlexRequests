using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;
using Xunit;

namespace PlexRequests.Tests;

public sealed class EpisodeOrderQueryTests
{
    [Fact]
    public void Canonical_episode_is_reversed_to_absolute_release_number()
    {
        var profile = Profile(EpisodeOrderType.Absolute, "A13 -> S02E01");

        Assert.True(EpisodeOrderMapping.TryTranslateCanonicalToSource(profile, 2, 1, out var source));
        Assert.Equal((0, 13), (source.Season, source.Episode));

        var queries = AcquisitionQuery.BuildScoped(new FulfillmentJobDto
        {
            MediaType = MediaType.TvShow,
            Title = "Example Anime",
            RequestedEpisodes = [new EpisodeRef { Season = 2, Episode = 1 }],
            EpisodeOrderProfile = profile
        });

        Assert.Equal(["Example Anime", "Example Anime 13"], queries);
        Assert.DoesNotContain(queries, x => x.Contains("season 2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dvd_order_season_target_prefers_the_source_season_pack()
    {
        var queries = AcquisitionQuery.BuildScoped(new FulfillmentJobDto
        {
            MediaType = MediaType.TvShow,
            Title = "Kids Show",
            SeasonTargets =
            [
                new SeasonTarget { Season = 2, EpisodeCount = 2, MissingEpisodes = [1, 2] }
            ],
            EpisodeOrderProfile = Profile(EpisodeOrderType.Dvd,
                "S01E13 -> S02E01\nS01E14 -> S02E02")
        });

        Assert.Equal("Kids Show", queries[0]);
        Assert.Equal("Kids Show season 1", queries[1]);
        Assert.Contains("Kids Show S01E13", queries);
        Assert.Contains("Kids Show S01E14", queries);
        Assert.DoesNotContain("Kids Show season 2", queries);
    }

    [Fact]
    public void Torznab_uses_generic_source_number_query_for_alternate_order()
    {
        var urls = TorznabIndexerProvider.BuildQueryUrls(new IndexerConfigDto
        {
            Url = "https://indexer.test/api",
            ApiKey = "secret",
            MediaCapabilities =
            [
                new IndexerMediaCapabilityDto
                    { MediaType = MediaType.Anime, Enabled = true, CategoriesCsv = "5070" }
            ]
        }, new FulfillmentJobDto
        {
            MediaType = MediaType.TvShow,
            IsAnime = true,
            Title = "Example Anime",
            RequestedEpisodes = [new EpisodeRef { Season = 2, Episode = 1 }],
            EpisodeOrderProfile = Profile(EpisodeOrderType.Absolute, "A13 -> S02E01")
        }).ToList();

        Assert.Equal(2, urls.Count);
        Assert.Contains("t=tvsearch", urls[0]);
        Assert.Contains("cat=5070", urls[0]);
        Assert.Contains("t=search", urls[1]);
        Assert.Contains("q=Example%20Anime%2013", urls[1]);
        Assert.DoesNotContain("season=2", string.Join('\n', urls));
    }

    [Fact]
    public void Ordinary_aired_order_keeps_existing_season_queries()
    {
        var queries = AcquisitionQuery.BuildScoped(new FulfillmentJobDto
        {
            MediaType = MediaType.TvShow,
            Title = "Ordinary Show",
            RequestedEpisodes = [new EpisodeRef { Season = 3, Episode = 7 }]
        });

        Assert.Equal(["Ordinary Show", "Ordinary Show season 3"], queries);
    }

    [Fact]
    public async Task Nyaa_searches_absolute_scope_and_deduplicates_overlapping_feed_rows()
    {
        const string hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var handler = new RecordingHandler($$"""
            <rss xmlns:nyaa="https://nyaa.si/xmlns/nyaa"><channel><item>
              <title>[Group] Example Anime - 13 [1080p]</title>
              <nyaa:infoHash>{{hash}}</nyaa:infoHash>
              <nyaa:seeders>20</nyaa:seeders><nyaa:leechers>1</nyaa:leechers><nyaa:size>1 GiB</nyaa:size>
            </item></channel></rss>
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://nyaa.si") };
        var provider = new NyaaIndexerProvider(http,
            new IndexerFetch(NullLogger<IndexerFetch>.Instance), Options.Create(new IndexerOptions()),
            NullLogger<NyaaIndexerProvider>.Instance);

        var results = await provider.SearchAsync(new IndexerConfigDto { Id = 9, Name = "Nyaa" },
            new FulfillmentJobDto
            {
                MediaType = MediaType.TvShow,
                Title = "Example Anime",
                RequestedEpisodes = [new EpisodeRef { Season = 2, Episode = 1 }],
                EpisodeOrderProfile = Profile(EpisodeOrderType.Absolute, "A13 -> S02E01")
            }, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(2, handler.Queries.Count);
        Assert.Contains("q=Example%20Anime&", handler.Queries[0]);
        Assert.Contains("q=Example%20Anime%2013&", handler.Queries[1]);
    }

    private static SeriesEpisodeOrderProfileDto Profile(EpisodeOrderType type, string mappings) => new()
    {
        TmdbId = 123,
        SeriesTitle = "Example",
        SourceOrder = type,
        MappingsText = mappings,
        Enabled = true
    };

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public List<string> Queries { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Queries.Add(request.RequestUri!.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body) });
        }
    }
}
