using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

public sealed class YtsIndexerProviderTests
{
    private const string Mirror = "https://movies-api.accel.li";
    private const string Hash1080 = "babdb6ed4bc3e2689b6fa58ce5950253c04552f7";

    // Live capture of GET https://movies-api.accel.li/api/v2/list_movies.json?query_term=tt0274761 (2026-10-24),
    // trimmed to the one movie. Field names and value shapes are exactly what the API returned, including the
    // upper-case hashes.
    private const string CaptureJson = """
        {"status":"ok","status_message":"Query was successful","data":{"movie_count":1,"limit":50,"page_number":1,"movies":[{"id":57992,"url":"https://yts.gg/movies/halloweentown-ii-kalabars-revenge-2001","imdb_code":"tt0274761","title":"Halloweentown II: Kalabar's Revenge","title_english":"Halloweentown II: Kalabar's Revenge","title_long":"Halloweentown II: Kalabar's Revenge (2001)","year":2001,"torrents":[{"url":"https://yts.gg/torrent/download/4572A86ED188C055B17510B3AB5BEF7C0735AC7E","hash":"4572A86ED188C055B17510B3AB5BEF7C0735AC7E","quality":"720p","type":"bluray","seeds":9,"peers":7,"size":"747.35 MB","size_bytes":783653274,"date_uploaded":"2023-11-27 02:33:44","date_uploaded_unix":1701048824,"video_codec":"x264","audio_channels":"2.0"},{"url":"https://yts.gg/torrent/download/BABDB6ED4BC3E2689B6FA58CE5950253C04552F7","hash":"BABDB6ED4BC3E2689B6FA58CE5950253C04552F7","quality":"1080p","type":"bluray","seeds":94,"peers":26,"size":"1.49 GB","size_bytes":1599875318,"date_uploaded":"2023-11-27 04:20:31","date_uploaded_unix":1701055231,"video_codec":"x264","audio_channels":"5.1"}]}]}}
        """;

    [Fact]
    public async Task Imdb_search_returns_the_1080p_bluray_torrent_with_a_magnet_for_its_hash()
    {
        var handler = new RecordingHandler(CaptureJson);
        using var http = new HttpClient(handler);
        var candidates = await Provider(http).SearchAsync(Indexer, Job(), CancellationToken.None);

        Assert.Equal($"{Mirror}/api/v2/list_movies.json?query_term=tt0274761&limit=50", Assert.Single(handler.Requested));
        Assert.Equal(2, candidates.Count);

        var c = Assert.Single(candidates, x => string.Equals(MagnetUtil.InfoHashFromMagnet(x.Acquisition.Locator), Hash1080, StringComparison.Ordinal));
        Assert.StartsWith("magnet:?xt=urn:btih:", c.Acquisition.Locator);
        Assert.Equal("Halloweentown II: Kalabar's Revenge (2001) 1080p bluray", c.ReleaseName);
        Assert.Equal("1080p", c.QualityLabel);
        Assert.Equal("tt0274761", c.ImdbId);
        Assert.Equal(94, c.Seeders);
        Assert.Equal(26, c.Leechers);
        Assert.Equal(1_599_875_318, c.SizeBytes);
        Assert.Equal(Hash1080, MagnetUtil.Normalize(c.Acquisition.SourceId));
    }

    [Fact]
    public async Task The_1080p_bluray_torrent_is_accepted_by_the_everyday_profile()
    {
        var candidates = await Provider(new HttpClient(new RecordingHandler(CaptureJson)))
            .SearchAsync(Indexer, Job(), CancellationToken.None);
        var c = Assert.Single(candidates, x => string.Equals(MagnetUtil.InfoHashFromMagnet(x.Acquisition.Locator), Hash1080, StringComparison.Ordinal));

        var defs = TestData.Definitions();
        var ranked = TestData.Evaluator().Evaluate(c, Job(), TestData.Context(EverydayProfile(defs), defs: defs));

        Assert.True(ranked.Accepted, ranked.Summary);
    }

    [Fact]
    public async Task The_hash_is_lower_case_in_the_source_id_and_the_magnet()
    {
        // YTS publishes the hash in upper case. The raw value must not reach the magnet, the source id, or the blocklist
        // key, because they are compared with the lower-case form everywhere else.
        var candidates = await Provider(new HttpClient(new RecordingHandler(CaptureJson)))
            .SearchAsync(Indexer, Job(), CancellationToken.None);
        var c = Assert.Single(candidates, x => string.Equals(MagnetUtil.InfoHashFromMagnet(x.Acquisition.Locator), Hash1080, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(Hash1080, c.Acquisition.SourceId, StringComparer.Ordinal);
        Assert.Contains($"xt=urn:btih:{Hash1080}", c.Acquisition.Locator, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_publish_date_comes_from_date_uploaded_so_the_max_age_setting_applies_to_yts()
    {
        var candidates = await Provider(new HttpClient(new RecordingHandler(CaptureJson)))
            .SearchAsync(Indexer, Job(), CancellationToken.None);
        var c = Assert.Single(candidates, x => string.Equals(MagnetUtil.InfoHashFromMagnet(x.Acquisition.Locator), Hash1080, StringComparison.OrdinalIgnoreCase));

        // date_uploaded_unix 1701055231 is 2023-11-27 03:20:31 UTC. The date_uploaded string (04:20:31) is in YTS's own
        // local time, so the Unix value is the one to trust.
        Assert.Equal(new DateTime(2023, 11, 27, 3, 20, 31, DateTimeKind.Utc), c.PublishDate);
    }

    private static YtsIndexerProvider Provider(HttpClient http) =>
        new(http, Options.Create(new IndexerOptions { YtsBaseUrlsCsv = Mirror }), NullLogger<YtsIndexerProvider>.Instance);

    private static IndexerConfigDto Indexer => new() { Id = 3, Name = "YTS", Implementation = "YTS" };

    private static FulfillmentJobDto Job() =>
        TestData.Job("Halloweentown II: Kalabar's Revenge", MediaType.Movie, Quality.FullHD, year: 2001, imdbId: "tt0274761");

    /// <summary>
    /// The seeded "Everyday" profile shape (Services/Implementations/QualityProfileSeeder.cs BuildProfile): WEBDL and
    /// Bluray at each resolution share one group item, the cutoff is WEBDL-1080p, and the floor is 720p.
    /// TestData.Profile does not group them, which would put Bluray above the cutoff and mask the real result.
    /// </summary>
    private static QualityProfileDto EverydayProfile(List<QualityDefinitionDto> defs)
    {
        var items = new List<QualityProfileItemDto>();
        foreach (var d in defs.Where(d => d.Source is not (ReleaseSource.Cam or ReleaseSource.BluRay)).OrderBy(d => d.SortWeight))
        {
            var allowed = d.Resolution >= (int)Quality.HD;
            if (d.Source == ReleaseSource.WebDl)
            {
                var bluray = defs.First(x => x.Resolution == d.Resolution && x.Source == ReleaseSource.BluRay);
                items.Add(new QualityProfileItemDto
                {
                    K = $"g:hq{d.Resolution}", N = $"WEBDL/Bluray-{d.Resolution}p", Allowed = allowed,
                    Members = new[] { d.Id, bluray.Id }
                });
            }
            else
            {
                items.Add(new QualityProfileItemDto { K = $"q:{d.Id}", N = d.Name, Allowed = allowed });
            }
        }
        return new QualityProfileDto
        {
            Id = 1,
            Name = "Everyday",
            Items = items,
            CutoffQualityDefinitionId = defs.First(d => d.Resolution == (int)Quality.FullHD && d.Source == ReleaseSource.WebDl).Id,
            UpgradeAllowed = true
        };
    }

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public List<string> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
