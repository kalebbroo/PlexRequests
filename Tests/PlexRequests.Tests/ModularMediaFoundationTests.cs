using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;
using Xunit;

namespace PlexRequests.Tests;

public class ModularMediaFoundationTests
{
    [Fact]
    public void MediaRef_UsesProviderNeutralStableIdentity()
    {
        var album = MediaRef.FromExternal(" MusicBrainz ", "ABCDEF01-1234", MediaType.Music, MediaKind.Album);

        Assert.True(album.IsValid);
        Assert.Equal("Music:Album:musicbrainz:abcdef01-1234", album.StableKey);
        Assert.False(album.TryGetTmdbId(out _));

        var youtubeA = MediaRef.FromExternal("youtube", "Ab_C", MediaType.Music, MediaKind.Track);
        var youtubeB = MediaRef.FromExternal("youtube", "ab_C", MediaType.Music, MediaKind.Track);
        Assert.NotEqual(youtubeA.StableKey, youtubeB.StableKey);

        var movie = MediaRef.FromTmdb(42, MediaType.Movie);
        Assert.True(movie.TryGetTmdbId(out var id));
        Assert.Equal(42, id);
        Assert.Equal("Movie:Movie:tmdb:42", movie.StableKey);
    }

    [Fact]
    public void Registry_AdvertisesMusicWithoutEnablingIncompleteRequests()
    {
        IMediaModule[] modules =
        [
            new MovieMediaModule(), new TelevisionMediaModule(), new AnimeMediaModule(), new MusicMediaModule()
        ];
        var registry = new MediaModuleRegistry(modules);

        Assert.True(registry.IsEnabled(MediaType.Movie));
        Assert.False(registry.IsEnabled(MediaType.Music));
        Assert.Contains(MediaKind.Album, registry.Get(MediaType.Music).Kinds);
        Assert.Contains(RequestScopeKind.ArtistCatalog, registry.Get(MediaType.Music).RequestScopes);
    }

    [Fact]
    public void IndexerCapabilities_AreDataDrivenAndKeepLegacyFallback()
    {
        var modern = new IndexerConfigDto
        {
            SupportsMovie = false,
            MediaCapabilities =
            [
                new() { MediaType = MediaType.Movie, Enabled = true, CategoriesCsv = "2999" },
                new() { MediaType = MediaType.Music, Enabled = true, CategoriesCsv = "3000,3010" }
            ]
        };

        Assert.True(modern.Supports(MediaType.Movie));
        Assert.True(modern.Supports(MediaType.Music));
        Assert.Equal("3000,3010", modern.CategoriesFor(MediaType.Music));

        var legacy = new IndexerConfigDto { SupportsMovie = true, SupportsTv = false };
        Assert.True(legacy.Supports(MediaType.Movie));
        Assert.False(legacy.Supports(MediaType.Music));
    }

    [Fact]
    public void BridgeRequest_AcceptsVersionOneAndVersionTwoIdentities()
    {
        var legacy = JsonSerializer.Deserialize<BridgeRequestBody>(
            """{"discordUserId":"7","mediaId":99,"mediaType":0}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(MediaRef.FromTmdb(99, MediaType.Movie), legacy.ResolveMedia());

        var current = JsonSerializer.Deserialize<BridgeRequestBody>(
            """{"discordUserId":"7","media":{"provider":"musicbrainz","id":"album-id","mediaType":2,"kind":2},"scope":{"kind":4}}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Music:Album:musicbrainz:album-id", current.ResolveMedia().StableKey);
        Assert.Equal(RequestScopeKind.Album, current.Scope!.Kind);
    }

    [Fact]
    public async Task IdentityResolver_ReusesCanonicalRowAndAddsAliases()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var service = new MediaIdentityService(db);

        var primary = await service.ResolveAsync(MediaRef.FromExternal(
            "musicbrainz", "release-group-id", MediaType.Music, MediaKind.Album));
        var repeated = await service.ResolveAsync(MediaRef.FromExternal(
            "MusicBrainz", "release-group-id", MediaType.Music, MediaKind.Album));
        await service.AddAliasAsync(primary.Id,
            MediaRef.FromExternal("plex", "rating-key-12", MediaType.Music, MediaKind.Album));

        Assert.Equal(primary.Id, repeated.Id);
        Assert.Equal(1, await db.MediaIdentities.CountAsync());
        Assert.Equal(2, await db.MediaExternalIdentifiers.CountAsync());
    }

    [Fact]
    public async Task Migration_BackfillsExistingRequestsJobsWatchlistAndIndexerCapabilities()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260818023839_AddDurableMonitorChildren");

        const string requestColumns = """
            MediaId, MediaType, Title, Status, RequestedAt, RequestAllSeasons, Monitored, MonitorMode,
            AutoMonitorNewSeasons, SearchForCutoffUpgrades, AchievedQuality, AchievedFormatScore,
            CutoffState, CutoffMet, UpgradeAttempts
            """;
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = $"""
            INSERT INTO MediaRequests ({requestColumns})
                VALUES (10, 0, 'Movie', 2, CURRENT_TIMESTAMP, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0);
            INSERT INTO MediaRequests ({requestColumns}, RequestedEpisodesCsv)
                VALUES (20, 1, 'Series', 2, CURRENT_TIMESTAMP, 0, 1, 1, 1, 1, 0, 0, 0, 1, 0, 'S1E2');
            INSERT INTO MediaRequests ({requestColumns}, ExternalId, ExternalSource)
                VALUES (0, 2, 'Album', 2, CURRENT_TIMESTAMP, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0,
                        'release-group-id', 'musicbrainz');

            INSERT INTO FulfillmentJobs
                (MediaRequestId, MediaId, MediaType, Title, Quality, Status, Attempts, Progress, CreatedAt,
                 IsAnime, IsUpgrade, Escalated, DeferCount, EmptySearchCount, IsManualGrab)
                VALUES (2, 20, 1, 'Series', 0, 0, 0, 0, CURRENT_TIMESTAMP, 0, 0, 0, 0, 0, 0);
            INSERT INTO Watchlist (MediaId, MediaType, Username, AddedAt)
                VALUES (10, 0, 'tester', CURRENT_TIMESTAMP);
            INSERT INTO MediaMetadataCache
                (MediaType, TmdbId, Title, GenresCsv, TotalSeasons, CardFetchedAt)
                VALUES (0, 10, 'Cached Movie', '', 0, CURRENT_TIMESTAMP);
            INSERT INTO Indexers
                (Name, Enabled, Priority, LastResultCount, ConsecutiveFailures, TotalSearches, TotalResults,
                 LastLatencyMs, CreatedAt, UpdatedAt, Implementation, SupportsMovie, SupportsTv, SupportsAnime)
                VALUES ('Fixture', 1, 25, 0, 0, 0, 0, 0, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP,
                        'Torznab', 1, 0, 1);
            INSERT INTO Indexers
                (Name, Enabled, Priority, LastResultCount, ConsecutiveFailures, TotalSearches, TotalResults,
                 LastLatencyMs, CreatedAt, UpdatedAt, Implementation, SupportsMovie, SupportsTv, SupportsAnime)
                VALUES ('General Fixture', 1, 30, 0, 0, 0, 0, 0, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP,
                        'PirateBay', 1, 1, 1);
            """;
            await seed.ExecuteNonQueryAsync();
        }

        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(3, await db.MediaRequests.CountAsync(x => x.MediaIdentityId != null));
        Assert.Equal(RequestScopeKind.Title, await db.MediaRequests.Where(x => x.MediaId == 10)
            .Select(x => x.RequestScopeKind).SingleAsync());
        Assert.Equal(RequestScopeKind.Episodes, await db.MediaRequests.Where(x => x.MediaId == 20)
            .Select(x => x.RequestScopeKind).SingleAsync());
        Assert.Equal(RequestScopeKind.Album, await db.MediaRequests.Where(x => x.MediaType == MediaType.Music)
            .Select(x => x.RequestScopeKind).SingleAsync());
        Assert.Equal(1, await db.FulfillmentJobs.CountAsync(x => x.MediaIdentityId != null));
        Assert.Equal(MediaKind.Series, await db.FulfillmentJobs.Select(x => x.MediaKind).SingleAsync());
        Assert.Equal(RequestScopeKind.Series, await db.FulfillmentJobs.Select(x => x.RequestScopeKind).SingleAsync());
        Assert.Equal(1, await db.Watchlist.CountAsync(x => x.MediaIdentityId != null));
        Assert.Equal(8, await db.IndexerMediaCapabilities.CountAsync());
        Assert.False(await db.IndexerMediaCapabilities.Where(x => x.MediaType == MediaType.Music
                && x.Indexer!.Implementation == "Torznab").Select(x => x.Enabled).SingleAsync());
        Assert.True(await db.IndexerMediaCapabilities.Where(x => x.MediaType == MediaType.Music
                && x.Indexer!.Implementation == "PirateBay").Select(x => x.Enabled).SingleAsync());
        Assert.Equal(1, await db.MediaMetadataCache.CountAsync(x => x.MediaIdentityId != null));
        Assert.False(await db.MusicSettings.Select(x => x.CatalogEnabled).SingleAsync());
        await using var foreignKeyCheck = connection.CreateCommand();
        foreignKeyCheck.CommandText = "PRAGMA foreign_key_check";
        await using var violations = await foreignKeyCheck.ExecuteReaderAsync();
        Assert.False(await violations.ReadAsync());
    }
}
