using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;
using Xunit;

namespace PlexRequests.Tests;

public sealed class MusicMetadataTests
{
    [Fact]
    public async Task Search_MapsAlbumsToCanonicalIdentityAndCoverArt()
    {
        const string json = """
            {"release-groups":[{"id":"ba991e16-9f65-43ac-931b-3a4cbc903cab","title":"Discovery",
            "first-release-date":"2001-03-12","primary-type":"Album","artist-credit":[{"name":"Daft Punk",
            "artist":{"id":"056e4f3e-d505-4dad-8ec1-d04f521cbb56","name":"Daft Punk"}}],
            "tags":[{"name":"electronic","count":9}]}]}
            """;
        var handler = new QueueHandler(json);
        var provider = CreateProvider(handler);

        var rows = await provider.SearchAsync(new MediaSearchQuery
        {
            Query = "Discovery", MediaType = MediaType.Music, Kind = MediaKind.Album
        });

        var album = Assert.Single(rows);
        Assert.Equal("Discovery", album.Title);
        Assert.Equal("Daft Punk", album.Subtitle);
        Assert.Equal(2001, album.Year);
        Assert.Equal("Music:Album:musicbrainz:ba991e16-9f65-43ac-931b-3a4cbc903cab",
            album.ResolveMediaRef().StableKey);
        Assert.Equal("https://coverartarchive.org/release-group/ba991e16-9f65-43ac-931b-3a4cbc903cab/front-500",
            album.PosterUrl);
        Assert.Contains("primarytype%3Aalbum", handler.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlbumDetails_SelectOfficialReleaseAndMapDiscTracklist()
    {
        const string group = """
            {"id":"rg-id","title":"Album","first-release-date":"2024-01-02","primary-type":"Album",
            "artist-credit":[{"name":"Artist","artist":{"id":"artist-id","name":"Artist"}}],
            "releases":[{"id":"bootleg","status":"Bootleg","date":"2023-01-01"},
                        {"id":"official","status":"Official","date":"2024-01-02"}]}
            """;
        const string release = """
            {"label-info":[{"label":{"name":"Example Records"}}],"media":[{"position":1,"tracks":[
            {"position":1,"title":"Opening","length":185000,"artist-credit":[{"name":"Artist"}],
             "recording":{"id":"track-id","title":"Opening","length":185000}}]}]}
            """;
        var handler = new QueueHandler(group, release);
        var provider = CreateProvider(handler);

        var detail = await provider.GetDetailsAsync(
            MediaRef.FromExternal("musicbrainz", "rg-id", MediaType.Music, MediaKind.Album));

        Assert.NotNull(detail);
        Assert.Equal("official", detail.Music!.ReleaseId);
        Assert.Equal("artist-id", detail.Music.ArtistId);
        Assert.Equal("Example Records", detail.Studio);
        var track = Assert.Single(detail.Music.Tracks);
        Assert.Equal("track-id", track.RecordingId);
        Assert.Equal(185000, track.DurationMs);
        Assert.Contains(handler.Requests, x => x.Contains("/release/official?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TransientFailure_IsRetriedWithoutLeakingAnErrorResult()
    {
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            JsonResponse("{\"artists\":[{\"id\":\"artist-id\",\"name\":\"Artist\"}]}"));
        var provider = CreateProvider(handler);

        var rows = await provider.SearchAsync(new MediaSearchQuery
        {
            Query = "Artist", MediaType = MediaType.Music, Kind = MediaKind.Artist
        });

        Assert.Single(rows);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ListenBrainzDiscovery_DeduplicatesAndRanksAlbumsByListeningCount()
    {
        const string json = """
            {"payload":{"release_groups":[
              {"release_group_mbid":"same-id","release_group_name":"Album","artist_name":"Artist","listen_count":20},
              {"release_group_mbid":"same-id","release_group_name":"Album","artist_name":"Artist","listen_count":30},
              {"release_group_mbid":"other-id","release_group_name":"Other","artist_name":"Other Artist","listen_count":10}
            ]}}
            """;
        var client = new ListenBrainzDiscoveryClient(
            new HttpClient(new QueueHandler(json)) { BaseAddress = new Uri("https://api.listenbrainz.org/") },
            new MemoryCache(new MemoryCacheOptions()), NullLogger<ListenBrainzDiscoveryClient>.Instance);

        var rows = await client.GetTrendingAlbumsAsync(1, 10);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Album", rows[0].Title);
        Assert.Equal("Music:Album:musicbrainz:same-id", rows[0].ResolveMediaRef().StableKey);
    }

    [Fact]
    public async Task MusicCatalogSetting_DefaultsOffAndUpdatesOneSingletonRow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var service = new MusicSettingsService(db);

        var defaults = await service.GetAsync();
        Assert.False(defaults.CatalogEnabled);
        Assert.False(defaults.RequestsEnabled);
        Assert.Equal("musicbrainz", defaults.MetadataProvider);
        await service.UpdateAsync(new MusicSettingsDto { CatalogEnabled = true, MetadataProvider = "youtube" });

        var updated = await service.GetAsync();
        Assert.True(updated.CatalogEnabled);
        Assert.False(updated.RequestsEnabled);
        Assert.Equal("youtube", updated.MetadataProvider);
        Assert.Equal(2, updated.ReadinessIssues.Count);
        Assert.Equal(1, await db.MusicSettings.CountAsync());
    }

    [Fact]
    public async Task ProviderNeutralCache_AllowsDistinctStringIdentityRowsWithoutSyntheticTmdbIds()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var identities = new MediaIdentityService(db);
        var first = await identities.ResolveAsync(MediaRef.FromExternal(
            "musicbrainz", "album-one", MediaType.Music, MediaKind.Album));
        var second = await identities.ResolveAsync(MediaRef.FromExternal(
            "musicbrainz", "album-two", MediaType.Music, MediaKind.Album));

        db.MediaMetadataCache.AddRange(
            new MediaMetadataCacheEntity
            {
                MediaType = MediaType.Music, TmdbId = 0, MediaIdentityId = first.Id,
                Title = "One", CardFetchedAt = DateTime.UtcNow
            },
            new MediaMetadataCacheEntity
            {
                MediaType = MediaType.Music, TmdbId = 0, MediaIdentityId = second.Id,
                Title = "Two", CardFetchedAt = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.MediaMetadataCache.CountAsync(x => x.MediaType == MediaType.Music));
        Assert.Equal(2, await db.MediaMetadataCache.Select(x => x.MediaIdentityId).Distinct().CountAsync());
    }

    private static MusicBrainzMetadataProvider CreateProvider(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://musicbrainz.org/") };
        return new MusicBrainzMetadataProvider(http, new MemoryCache(new MemoryCacheOptions()),
            new ImmediateRateGate(), new EmptyDiscovery(), NullLogger<MusicBrainzMetadataProvider>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class ImmediateRateGate : IMusicBrainzRateGate
    {
        public ValueTask WaitAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class EmptyDiscovery : IListenBrainzDiscoveryClient
    {
        public Task<List<PlexRequestsHosted.Shared.DTOs.MediaCardDto>> GetTrendingAlbumsAsync(int page,
            int pageSize, CancellationToken cancellationToken = default) => Task.FromResult(new List<PlexRequestsHosted.Shared.DTOs.MediaCardDto>());
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;
        public List<string> Requests { get; } = new();

        public QueueHandler(params string[] json) : this(json.Select(x => JsonResponse(x)).ToArray()) { }

        public QueueHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<Func<HttpResponseMessage>>(responses.Select<HttpResponseMessage, Func<HttpResponseMessage>>(response =>
            {
                var status = response.StatusCode;
                var body = response.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                var mediaType = response.Content?.Headers.ContentType?.MediaType;
                response.Dispose();
                return () => new HttpResponseMessage(status)
                {
                    Content = body is null ? null : new StringContent(body, System.Text.Encoding.UTF8,
                        mediaType ?? "application/json")
                };
            }));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            if (_responses.Count == 0) throw new InvalidOperationException("No fake response remains.");
            return Task.FromResult(_responses.Dequeue()());
        }
    }
}
