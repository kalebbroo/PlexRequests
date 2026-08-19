using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;
using Xunit;

namespace PlexRequests.Tests;

public sealed class BridgeIntegrationTests
{
    [Fact]
    public async Task Outbox_repairs_missing_music_event_and_delivers_canonical_identity_once()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var user = new UserEntity { Username = "listener" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        db.UserProfiles.Add(new UserProfileEntity
        {
            UserId = user.Id,
            PlexUsername = user.Username,
            DiscordUserId = "123456789012345678",
            DiscordDmOptIn = true
        });
        var request = new MediaRequestEntity
        {
            MediaId = 0,
            MediaType = MediaType.Music,
            RequestScopeKind = RequestScopeKind.Album,
            ExternalSource = "musicbrainz",
            ExternalId = "BA991E16-9F65-43AC-931B-3A4CBC903CAB",
            Title = "Discovery",
            Status = RequestStatus.Available,
            AvailableAt = DateTime.UtcNow,
            RequestedByUserId = user.Id,
            RequestedBy = user.Username
        };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();

        var metadata = new RecordingMetadataProvider();
        var service = CreateOutbox(db, metadata);

        var first = await service.ReadBatchAsync(0, 25);
        var bridgeEvent = Assert.Single(first.Events);
        Assert.Equal(BridgeEventType.Available, bridgeEvent.Type);
        Assert.Equal("Music:Album:musicbrainz:ba991e16-9f65-43ac-931b-3a4cbc903cab",
            bridgeEvent.Media!.StableKey);
        Assert.Equal("123456789012345678", bridgeEvent.RequesterDiscordId);
        Assert.Equal(bridgeEvent.Media, metadata.LastDetailsRequest);
        Assert.False(string.IsNullOrWhiteSpace(bridgeEvent.EventId));

        // Repair and an explicit duplicate write converge on the same stable event key.
        await service.EnqueueAsync(new MediaRequestDto { Id = request.Id }, BridgeEventType.Available);
        Assert.Equal(1, await db.BridgeOutbox.CountAsync());

        var acknowledgement = await service.AcknowledgeAsync(first.NextCursor);
        Assert.Equal(1, acknowledgement.Acknowledged);
        Assert.NotNull(await db.BridgeOutbox.Select(x => x.AcknowledgedAt).SingleAsync());
    }

    [Fact]
    public async Task Link_code_is_single_use_and_relink_keeps_one_discord_owner()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var firstUser = new UserEntity { Username = "first" };
        var secondUser = new UserEntity { Username = "second" };
        db.Users.AddRange(firstUser, secondUser);
        await db.SaveChangesAsync();
        db.UserProfiles.AddRange(
            new UserProfileEntity { UserId = firstUser.Id, PlexUsername = firstUser.Username },
            new UserProfileEntity { UserId = secondUser.Id, PlexUsername = secondUser.Username });
        await db.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var links = new DiscordLinkService(db, cache, NullLogger<DiscordLinkService>.Instance);
        const string discordId = "123456789012345678";

        var firstCode = links.GenerateLinkCode(firstUser.Id);
        Assert.True((await links.CompleteLinkAsync(firstCode, discordId, "listener")).Success);
        Assert.False((await links.CompleteLinkAsync(firstCode, discordId, "listener")).Success);

        var secondCode = links.GenerateLinkCode(secondUser.Id);
        Assert.True((await links.CompleteLinkAsync(secondCode, discordId, "listener")).Success);
        var owners = await db.UserProfiles.Where(x => x.DiscordUserId == discordId).ToListAsync();
        Assert.Equal(secondUser.Id, Assert.Single(owners).UserId);
        Assert.False(await db.UserProfiles.Where(x => x.UserId == firstUser.Id)
            .Select(x => x.DiscordDmOptIn).SingleAsync());
    }

    [Theory]
    [InlineData("12345678901234567", true)]
    [InlineData("12345678901234567890", true)]
    [InlineData("1234", false)]
    [InlineData("12345678901234567x", false)]
    [InlineData("123456789012345678901", false)]
    public void Discord_snowflake_validation_is_strict(string value, bool expected) =>
        Assert.Equal(expected, DiscordLinkService.IsValidDiscordUserId(value));

    private static BridgeOutboxService CreateOutbox(AppDbContext db, IMediaMetadataProvider metadata)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bridge:Enabled"] = "true",
            ["Bridge:EventRetentionDays"] = "30"
        }).Build();
        var services = new ServiceCollection().AddSingleton<IMediaMetadataProvider>(metadata).BuildServiceProvider();
        return new BridgeOutboxService(db, services, configuration,
            NullLogger<BridgeOutboxService>.Instance);
    }

    private sealed class RecordingMetadataProvider : IMediaMetadataProvider
    {
        public MediaRef? LastDetailsRequest { get; private set; }

        public Task<List<MediaCardDto>> SearchAsync(string query, MediaType? mediaType = null, int page = 1,
            int pageSize = 20) => Task.FromResult(new List<MediaCardDto>());

        public Task<MediaDetailDto?> GetDetailsAsync(int mediaId, MediaType mediaType) =>
            Task.FromResult<MediaDetailDto?>(null);

        public Task<MediaDetailDto?> GetDetailsAsync(MediaRef mediaRef)
        {
            LastDetailsRequest = mediaRef;
            return Task.FromResult<MediaDetailDto?>(new MediaDetailDto
            {
                Id = 0,
                MediaType = mediaRef.MediaType,
                Title = "Discovery",
                Overview = "Album details"
            });
        }

        public Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10) =>
            Task.FromResult(new List<MediaCardDto>());

        public Task<List<MediaCardDto>> GetLibraryAsync(MediaType mediaType, int page = 1,
            int pageSize = 20) => Task.FromResult(new List<MediaCardDto>());

        public Task<string?> GetImdbIdAsync(int mediaId, MediaType mediaType) =>
            Task.FromResult<string?>(null);
    }
}
