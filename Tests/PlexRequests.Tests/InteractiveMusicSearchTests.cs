using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;
using Xunit;

namespace PlexRequests.Tests;

public sealed class InteractiveMusicSearchTests
{
    [Fact]
    public async Task LegacyAdminEntryPoint_RestoresMusicIdentityAndAcquisitionSnapshot()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var request = new MediaRequestEntity
        {
            MediaId = 0,
            MediaType = MediaType.Music,
            RequestScopeKind = RequestScopeKind.Album,
            ExternalSource = "musicbrainz",
            ExternalId = "release-group-id",
            Title = "Album"
        };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();

        var music = new MusicAcquisitionContextDto
        {
            SchemaVersion = 2,
            Artist = "Artist",
            Album = "Album",
            Tracks = [new MusicTrackMetadataDto { Title = "Track", TrackNumber = 1 }]
        };
        db.FulfillmentJobs.Add(new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaId = 0,
            MediaType = MediaType.Music,
            MediaKind = MediaKind.Album,
            RequestScopeKind = RequestScopeKind.Album,
            ExternalSource = "musicbrainz",
            ExternalId = "release-group-id",
            AcquisitionContextJson = JsonSerializer.Serialize(music),
            Title = "Album",
            Quality = Quality.Any
        });
        await db.SaveChangesAsync();

        var profiles = new QualityProfileService(db, NullLogger<QualityProfileService>.Instance);
        var service = new InteractiveSearchService(db, profiles, NullLogger<InteractiveSearchService>.Instance);

        await service.CreateAsync(request.Id, MediaType.Music, 0, "Album", null, null, null, null, null);
        var claimed = Assert.IsType<SearchTaskDto>(await service.ClaimAsync("test-worker"));
        var media = Assert.IsType<MediaRef>(claimed.Media);

        Assert.Equal("Music:Album:musicbrainz:release-group-id", media.StableKey);
        Assert.Equal(RequestScopeKind.Album, claimed.RequestScope);
        Assert.Equal("Artist", claimed.Music?.Artist);
        Assert.Equal("Track", Assert.Single(claimed.Music!.Tracks).Title);
    }
}
