using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;
using Xunit;

namespace PlexRequests.Tests;

public sealed class PlexLibraryInventoryTests
{
    [Fact]
    public async Task LatestMigrationCreatesTheExactPartInventory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).Options);

        await db.Database.MigrateAsync();
        db.PlexLibraryFiles.Add(new()
        {
            SectionKey = "1",
            SectionTitle = "Movies",
            RatingKey = "10",
            PlexPartKey = "20",
            FilePath = "/library/movie.mkv",
            MediaType = MediaType.Movie,
            Title = "Movie"
        });
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.PlexLibraryFiles.CountAsync());
    }

    [Fact]
    public void ExactPartParserKeepsMeasuredVersionsAndRejectsRelativePaths()
    {
        using var document = JsonDocument.Parse("""
            {
              "Media": [
                {
                  "width": 1920, "height": 800, "videoCodec": "hevc", "audioCodec": "eac3",
                  "Part": [
                    { "id": "11", "file": "/library/Movie/Movie.mkv", "size": 1200 },
                    { "file": "relative/movie.mkv", "size": 900 }
                  ]
                },
                {
                  "width": 3840, "height": 1600, "videoCodec": "h264", "audioCodec": "aac",
                  "Part": [{ "file": "D:\\Movies\\Movie UHD.mkv", "size": 2400 }]
                }
              ]
            }
            """);

        var parts = PlexApiService.ExtractLibraryParts(document.RootElement);

        Assert.Equal(2, parts.Count);
        Assert.Equal(("11", "/library/Movie/Movie.mkv", 1200L, 1080, "HEVC", "EAC3"),
            (parts[0].PartKey, parts[0].FilePath, parts[0].SizeBytes, parts[0].ResolutionHeight,
                parts[0].VideoCodec, parts[0].AudioCodec));
        Assert.StartsWith("path-", parts[1].PartKey);
        Assert.Equal(2160, parts[1].ResolutionHeight);
        Assert.Equal("H.264", parts[1].VideoCodec);
    }

    [Fact]
    public async Task AvailabilityScanPersistsExactPartsAndRequiresThreeTrustworthyMissesToPrune()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        var handler = new InventoryHandler();
        using var http = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new PlexApiService(new EmptyMetadata(), http,
            Options.Create(new PlexConfiguration
            {
                PrimaryServerUrl = "http://plex.test:32400",
                ServerToken = "test-token"
            }), cache, NullLogger<PlexApiService>.Instance, db, new EmptySeasonEvaluator());

        await service.RebuildAvailabilityFromPlexAsync();

        Assert.Equal(4, await db.PlexLibraryFiles.CountAsync());
        var first = await db.PlexLibraryFiles.SingleAsync(file => file.RatingKey == "101");
        Assert.Equal(("Movies", "/library/Movie 1/Movie 1.mkv", 1_000L, 1080, "HEVC", "EAC3"),
            (first.SectionTitle, first.FilePath, first.SizeBytes, first.ResolutionHeight,
                first.VideoCodec, first.AudioCodec));
        Assert.Equal(0, first.MissedScans);

        handler.IncludeFirst = false;
        for (var pass = 1; pass <= 2; pass++)
        {
            await service.RebuildAvailabilityFromPlexAsync();
            db.ChangeTracker.Clear();
            Assert.Equal(pass, (await db.PlexLibraryFiles.SingleAsync(file => file.RatingKey == "101")).MissedScans);
        }

        await service.RebuildAvailabilityFromPlexAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(3, await db.PlexLibraryFiles.CountAsync());
        Assert.False(await db.PlexLibraryFiles.AnyAsync(file => file.RatingKey == "101"));
    }

    [Fact]
    public async Task AvailabilityScanCarriesSeriesAndEpisodeIdentityOntoExactParts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        using var http = new HttpClient(new EpisodeInventoryHandler());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new PlexApiService(new EmptyMetadata(), http,
            Options.Create(new PlexConfiguration
            {
                PrimaryServerUrl = "http://plex.test:32400",
                ServerToken = "test-token"
            }), cache, NullLogger<PlexApiService>.Instance, db, new EmptySeasonEvaluator());

        await service.RebuildAvailabilityFromPlexAsync();

        var file = Assert.Single(await db.PlexLibraryFiles.ToListAsync());
        Assert.Equal(("episode-2", "show-1", MediaType.TvShow, "Example Show", 1, 2),
            (file.RatingKey, file.ShowRatingKey, file.MediaType, file.Title,
                file.SeasonNumber, file.EpisodeNumber));
        Assert.Equal("/library/Example Show/Season 01/Example Show - s01e02.mkv", file.FilePath);
    }

    private sealed class InventoryHandler : HttpMessageHandler
    {
        public bool IncludeFirst { get; set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var json = path switch
            {
                "/library/sections" =>
                    "{\"MediaContainer\":{\"Directory\":[{\"key\":\"1\",\"type\":\"movie\",\"title\":\"Movies\"}]}}",
                "/library/sections/1/collections" => "{\"MediaContainer\":{\"size\":0}}",
                "/library/sections/1/all" => LibraryItems(),
                _ => "{\"MediaContainer\":{}}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        private string LibraryItems()
        {
            var first = IncludeFirst ? Movie(1) + "," : string.Empty;
            return $"{{\"MediaContainer\":{{\"Metadata\":[{first}{Movie(2)},{Movie(3)},{Movie(4)}]}}}}";
        }

        private static string Movie(int number) => $$"""
            {"ratingKey":"10{{number}}","title":"Movie {{number}}","year":202{{number}},
             "Guid":[{"id":"tmdb://10{{number}}"}],
             "Media":[{"width":1920,"height":800,"videoResolution":"1080","videoCodec":"hevc",
                       "audioCodec":"eac3","Part":[{"id":"20{{number}}","file":"/library/Movie {{number}}/Movie {{number}}.mkv","size":1000}]}]}
            """;
    }

    private sealed class EpisodeInventoryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var isEpisodeQuery = request.RequestUri.Query.Contains("type=4", StringComparison.Ordinal);
            var json = path switch
            {
                "/library/sections" =>
                    "{\"MediaContainer\":{\"Directory\":[{\"key\":\"2\",\"type\":\"show\",\"title\":\"TV Shows\"}]}}",
                "/library/sections/2/collections" => "{\"MediaContainer\":{\"size\":0}}",
                "/library/sections/2/all" when isEpisodeQuery => """
                    {"MediaContainer":{"Metadata":[{
                      "ratingKey":"episode-2","grandparentRatingKey":"show-1",
                      "grandparentTitle":"Example Show","title":"Second Episode","year":2026,
                      "parentIndex":1,"index":2,
                      "Media":[{"width":1280,"height":720,"videoCodec":"h264","audioCodec":"aac",
                                "Part":[{"id":"part-2","file":"/library/Example Show/Season 01/Example Show - s01e02.mkv","size":750}]}]
                    }]}}
                    """,
                "/library/sections/2/all" => """
                    {"MediaContainer":{"Metadata":[{
                      "ratingKey":"show-1","title":"Example Show","year":2026,
                      "Guid":[{"id":"tmdb://500"}]
                    }]}}
                    """,
                _ => "{\"MediaContainer\":{}}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class EmptySeasonEvaluator : ISeasonAvailabilityEvaluator
    {
        public Task<string?> ResolveRatingKeyAsync(int tmdbShowId, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task<Dictionary<int, HashSet<int>>> GetPlexEpisodesAsync(int tmdbShowId,
            CancellationToken ct = default) => Task.FromResult(new Dictionary<int, HashSet<int>>());
        public Task<Dictionary<int, SeasonCompleteness>> EvaluateAsync(int tmdbShowId,
            CancellationToken ct = default) => Task.FromResult(new Dictionary<int, SeasonCompleteness>());
        public Task<List<int>> GetCompleteSeasonsAsync(int tmdbShowId, CancellationToken ct = default) =>
            Task.FromResult(new List<int>());
        public Task<bool> IsWholeSeriesSatisfiedAsync(int tmdbShowId, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class EmptyMetadata : IMediaMetadataProvider
    {
        public Task<List<MediaCardDto>> SearchAsync(string query, MediaType? mediaType = null,
            int page = 1, int pageSize = 20) => Task.FromResult(new List<MediaCardDto>());
        public Task<MediaDetailDto?> GetDetailsAsync(int mediaId, MediaType mediaType) =>
            Task.FromResult<MediaDetailDto?>(null);
        public Task<MediaDetailDto?> GetDetailsAsync(MediaRef mediaRef) =>
            Task.FromResult<MediaDetailDto?>(null);
        public Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10) =>
            Task.FromResult(new List<MediaCardDto>());
        public Task<List<MediaCardDto>> GetLibraryAsync(MediaType mediaType, int page = 1,
            int pageSize = 20) => Task.FromResult(new List<MediaCardDto>());
        public Task<string?> GetImdbIdAsync(int mediaId, MediaType mediaType) =>
            Task.FromResult<string?>(null);
    }
}
