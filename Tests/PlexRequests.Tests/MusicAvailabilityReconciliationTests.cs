using System.Text.Json;
using PlexRequestsHosted.Services.Background;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;
using Xunit;

namespace PlexRequests.Tests;

public sealed class MusicAvailabilityReconciliationTests
{
    [Theory]
    [InlineData(RequestScopeKind.Album, MediaType.Music, MediaKind.Album)]
    [InlineData(RequestScopeKind.ArtistCatalog, MediaType.Music, MediaKind.Artist)]
    [InlineData(RequestScopeKind.Track, MediaType.Music, MediaKind.Track)]
    [InlineData(RequestScopeKind.Seasons, MediaType.TvShow, MediaKind.Series)]
    [InlineData(RequestScopeKind.Episodes, MediaType.Anime, MediaKind.Series)]
    [InlineData(RequestScopeKind.ArtistCatalog, MediaType.Movie, MediaKind.Movie)]
    public void Request_scope_has_one_shared_media_kind_mapping(
        RequestScopeKind scope, MediaType mediaType, MediaKind expected) =>
        Assert.Equal(expected, scope.ToMediaKind(mediaType));

    [Fact]
    public void Malformed_incomplete_and_wrong_scope_history_cannot_hide_an_older_valid_contract()
    {
        var valid = Album("Expected Album");
        var selected = AvailabilityReconciliationService.SelectMusicCompletionContext(
            RequestScopeKind.Album,
            [
                "{not-json",
                JsonSerializer.Serialize(Artist()),
                "{\"SchemaVersion\":2,\"Kind\":2,\"Artist\":\"Artist\",\"Album\":\"Incomplete\",\"Tracks\":null}",
                JsonSerializer.Serialize(valid)
            ]);

        Assert.NotNull(selected);
        Assert.Equal("Expected Album", selected.Album);
        Assert.True(selected.HasCompletionContract);
    }

    [Fact]
    public void Newest_complete_scope_correct_contract_wins()
    {
        var selected = AvailabilityReconciliationService.SelectMusicCompletionContext(
            RequestScopeKind.Album,
            [JsonSerializer.Serialize(Album("Newest")), JsonSerializer.Serialize(Album("Older"))]);

        Assert.Equal("Newest", selected?.Album);
    }

    [Theory]
    [InlineData(RequestScopeKind.Album)]
    [InlineData(RequestScopeKind.Track)]
    [InlineData(RequestScopeKind.ArtistCatalog)]
    public void A_complete_contract_for_another_scope_is_never_reused(RequestScopeKind scope)
    {
        var wrong = scope switch
        {
            RequestScopeKind.Album => Track(),
            RequestScopeKind.Track => Artist(),
            _ => Album("Album")
        };

        var selected = AvailabilityReconciliationService.SelectMusicCompletionContext(
            scope, [JsonSerializer.Serialize(wrong)]);

        Assert.Null(selected);
    }

    [Fact]
    public void Legacy_or_explicitly_null_contracts_wait_for_queue_enrichment()
    {
        var legacy = Album("Legacy");
        legacy.SchemaVersion = 1;

        var selected = AvailabilityReconciliationService.SelectMusicCompletionContext(
            RequestScopeKind.Album,
            [null, " ", JsonSerializer.Serialize(legacy),
                "{\"SchemaVersion\":2,\"Kind\":2,\"Artist\":\"Artist\",\"Album\":\"Null\",\"Tracks\":null}"]);

        Assert.Null(selected);
    }

    private static MusicAcquisitionContextDto Album(string title) => new()
    {
        SchemaVersion = 2,
        Kind = MediaKind.Album,
        Artist = "Artist",
        Album = title,
        TrackCount = 1,
        Tracks = [new MusicTrackMetadataDto { Title = "Track", DiscNumber = 1, TrackNumber = 1 }]
    };

    private static MusicAcquisitionContextDto Track() => new()
    {
        SchemaVersion = 2,
        Kind = MediaKind.Track,
        Artist = "Artist",
        Track = "Track",
        TrackCount = 1,
        Tracks = [new MusicTrackMetadataDto { Title = "Track", DiscNumber = 1, TrackNumber = 1 }]
    };

    private static MusicAcquisitionContextDto Artist() => new()
    {
        SchemaVersion = 2,
        Kind = MediaKind.Artist,
        Artist = "Artist",
        ExpectedAlbums = ["Album"]
    };
}
