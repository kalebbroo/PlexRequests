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
    public void Reassigning_profile_identity_clears_every_stale_mapping_provenance_field()
    {
        var profile = Profile(EpisodeOrderType.Absolute, "A13 -> S02E01");
        profile.SourceEpisodeGroupId = "old-group";
        profile.SourceEpisodeGroupName = "Old order";
        profile.ImportedAt = DateTime.UtcNow;
        profile.SourceGroups = [new EpisodeOrderSourceGroupDto { SourceSeason = 1, Name = "Old arc" }];

        EpisodeOrderMapping.AssignSeries(profile, 456, "Different Show");

        Assert.Equal(456, profile.TmdbId);
        Assert.Equal("Different Show", profile.SeriesTitle);
        Assert.Empty(profile.MappingsText);
        Assert.Null(profile.SourceEpisodeGroupId);
        Assert.Null(profile.SourceEpisodeGroupName);
        Assert.Null(profile.ImportedAt);
        Assert.Empty(profile.SourceGroups);
    }

    [Fact]
    public void Imported_mapping_cannot_reference_an_unnamed_source_group()
    {
        var profile = Profile(EpisodeOrderType.Custom,
            "S01E01 -> S01E01\nS02E01 -> S01E02");
        profile.SourceGroups = [new EpisodeOrderSourceGroupDto { SourceSeason = 1, Name = "Only arc" }];

        Assert.False(EpisodeOrderMapping.TryParse(profile, out _, out var error));
        Assert.Contains("no matching imported group", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacySeasonBasedTmdbImportMustBeReimportedForFolderTitleEvidence()
    {
        var profile = Profile(EpisodeOrderType.Custom, "S01E01 -> S01E01");
        profile.SourceEpisodeGroupId = "legacy-group";

        Assert.False(EpisodeOrderMapping.TryParse(profile, out _, out var error));
        Assert.Contains("re-import", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Standalone_arc_name_resolves_its_configured_source_group_and_file_mapping()
    {
        var profile = CustomArcProfile();
        const string path = "/downloads/[MTBB] MONOGATARI Series OFF & MONSTER Season S01/"
                            + "[MTBB] MONOGATARI Series OFF & MONSTER Season - 01.mkv";

        Assert.True(EpisodeOrderMapping.TryResolveNamedSourceGroup(profile, path, out var sourceSeason));
        Assert.Equal(16, sourceSeason);
        Assert.True(EpisodeOrderMapping.TryTranslateFile(profile, path, parsedSeason: 1, sourceEpisode: 1,
            out var target));
        Assert.Equal((5, 1), (target.Season, target.Episode));
    }

    [Fact]
    public void Overlapping_source_group_names_fail_closed()
    {
        var profile = CustomArcProfile();
        profile.SourceGroups =
        [
            new EpisodeOrderSourceGroupDto { SourceSeason = 13, Name = "Owarimonogatari" },
            new EpisodeOrderSourceGroupDto { SourceSeason = 14, Name = "Owarimonogatari S1" }
        ];

        Assert.False(EpisodeOrderMapping.TryResolveNamedSourceGroup(profile,
            "[Group] Owarimonogatari S1 - 01 [1080p]", out _));
    }

    [Fact]
    public void Alternate_order_search_includes_the_named_arc_before_numeric_fallbacks()
    {
        var queries = AcquisitionQuery.BuildScoped(new FulfillmentJobDto
        {
            MediaType = MediaType.TvShow,
            IsAnime = true,
            Title = "Monogatari",
            SeasonTargets =
            [
                new SeasonTarget { Season = 5, EpisodeCount = 1, MissingEpisodes = [1] }
            ],
            EpisodeOrderProfile = CustomArcProfile()
        });

        Assert.Equal("Monogatari MONOGATARI Series OFF & MONSTER Season", queries[1]);
        Assert.Contains("Monogatari season 16", queries);
    }

    [Fact]
    public void Renaming_the_same_profile_identity_preserves_its_validated_mapping()
    {
        var profile = Profile(EpisodeOrderType.Absolute, "A13 -> S02E01");
        profile.SourceEpisodeGroupId = "validated-group";

        EpisodeOrderMapping.AssignSeries(profile, profile.TmdbId, "Corrected title");

        Assert.Equal("Corrected title", profile.SeriesTitle);
        Assert.Equal("A13 -> S02E01", profile.MappingsText);
        Assert.Equal("validated-group", profile.SourceEpisodeGroupId);
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

    private static SeriesEpisodeOrderProfileDto CustomArcProfile() => new()
    {
        TmdbId = 46195,
        SeriesTitle = "Monogatari",
        SourceOrder = EpisodeOrderType.Custom,
        Enabled = true,
        CustomMetadataEnabled = true,
        SourceGroups =
        [
            new EpisodeOrderSourceGroupDto
                { SourceSeason = 16, Name = "MONOGATARI Series OFF & MONSTER Season" }
        ],
        CustomEpisodes =
        [
            new CustomEpisodeMetadataDto
            {
                SourceSeason = 16, SourceEpisode = 1, Season = 5, Episode = 1,
                ContentKind = "episode", IncludeInMonitoring = true
            }
        ]
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
