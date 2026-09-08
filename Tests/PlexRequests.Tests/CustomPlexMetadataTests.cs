using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;
using Xunit;

namespace PlexRequests.Tests;

public sealed class CustomPlexMetadataTests
{
    [Fact]
    public async Task SavedCustomMetadataUpdatesAndLocksOnlyExistingPlexTargets()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var profile = new SeriesEpisodeOrderProfileDto
        {
            TmdbId = 46195,
            SeriesTitle = "Monogatari",
            SourceOrder = EpisodeOrderType.Custom,
            CustomMetadataEnabled = true,
            Enabled = true,
            CustomSeasons = [new() { Season = 0, Name = "Story Specials" }],
            CustomEpisodes =
            [
                new()
                {
                    SourceSeason = 1, SourceEpisode = 13, Season = 0, Episode = 2,
                    Title = "Tsubasa Cat, Part Three", Summary = "Custom summary",
                    OriginallyAvailableAt = new DateTime(2009, 11, 3), ContentKind = "ONA"
                },
                new()
                {
                    SourceSeason = 1, SourceEpisode = 14, Season = 0, Episode = 3,
                    Title = "Not indexed yet", ContentKind = "ONA"
                }
            ]
        };
        db.LibraryOrganizationPreferences.Add(new LibraryOrganizationPreferencesEntity
        {
            IsSingleton = true,
            SeriesEpisodeOrderProfilesJson = JsonSerializer.Serialize(new[] { profile })
        });
        await db.SaveChangesAsync();

        var handler = new PlexMetadataHandler();
        using var http = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new PlexApiService(new EmptyMetadata(), http,
            Options.Create(new PlexConfiguration
            {
                PrimaryServerUrl = "http://plex.test:32400",
                ServerToken = "test-token"
            }), cache, NullLogger<PlexApiService>.Instance, db, new FixedSeasonEvaluator());

        var result = await service.ApplyCustomMetadataAsync(46195);

        Assert.True(result.Configured);
        Assert.True(result.SeriesFound);
        Assert.Equal(1, result.SeasonsUpdated);
        Assert.Equal(1, result.EpisodesUpdated);
        Assert.Equal(["S00E03"], result.MissingTargets);
        Assert.Empty(result.Errors);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Put
            && request.Uri.Contains("type=3")
            && request.Uri.Contains("title.value=Story%20Specials")
            && request.Uri.Contains("title.locked=1"));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Put
            && request.Uri.Contains("type=4")
            && request.Uri.Contains("title.value=Tsubasa%20Cat%2C%20Part%20Three")
            && request.Uri.Contains("summary.value=Custom%20summary")
            && request.Uri.Contains("originallyAvailableAt.value=2009-11-03"));
        Assert.All(handler.Requests, request => Assert.Equal("test-token", request.Token));
    }

    private sealed class PlexMetadataHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri, string? Token)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.OriginalString;
            Requests.Add((request.Method, uri,
                request.Headers.TryGetValues("X-Plex-Token", out var tokens) ? tokens.Single() : null));
            var json = request.RequestUri.AbsolutePath switch
            {
                "/library/metadata/show" =>
                    "{\"MediaContainer\":{\"Metadata\":[{\"librarySectionID\":9}]}}",
                "/library/metadata/show/children" =>
                    "{\"MediaContainer\":{\"Metadata\":[{\"index\":0,\"ratingKey\":\"season-0\"}]}}",
                "/library/metadata/season-0/children" =>
                    "{\"MediaContainer\":{\"Metadata\":[{\"index\":2,\"ratingKey\":\"episode-2\"}]}}",
                _ => "{}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FixedSeasonEvaluator : ISeasonAvailabilityEvaluator
    {
        public Task<string?> ResolveRatingKeyAsync(int tmdbShowId, CancellationToken ct = default) =>
            Task.FromResult<string?>("show");
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
        public Task<MediaDetailDto?> GetDetailsAsync(MediaRef mediaRef) => Task.FromResult<MediaDetailDto?>(null);
        public Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10) =>
            Task.FromResult(new List<MediaCardDto>());
        public Task<List<MediaCardDto>> GetLibraryAsync(MediaType mediaType, int page = 1,
            int pageSize = 20) => Task.FromResult(new List<MediaCardDto>());
        public Task<string?> GetImdbIdAsync(int mediaId, MediaType mediaType) => Task.FromResult<string?>(null);
    }
}
