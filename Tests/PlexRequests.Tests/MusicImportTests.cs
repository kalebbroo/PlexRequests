using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Organize;
using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

public sealed class MusicImportTests
{
    [Fact]
    public async Task Album_uses_metadata_track_order_and_plex_layout()
    {
        var root = NewRoot();
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "download")).FullName;
            var library = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
            await File.WriteAllTextAsync(Path.Combine(source, "02 - Second.flac"), "second");
            await File.WriteAllTextAsync(Path.Combine(source, "01 - First.flac"), "first");

            var result = await CreateOrganizer().OrganizeAsync(AlbumJob(),
                new TransferItem("torrent", null, null, true), source, Preferences(library), CancellationToken.None);

            Assert.True(result.Success, result.FailReason);
            Assert.Equal(2, result.MediaFileCount);
            Assert.True(File.Exists(Path.Combine(library, "Artist", "Album (2026)", "01-01 - First.flac")));
            Assert.True(File.Exists(Path.Combine(library, "Artist", "Album (2026)", "01-02 - Second.flac")));
            Assert.All(result.Files, f => Assert.Equal("audio", f.FileType));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Incomplete_album_is_rejected_before_any_library_write()
    {
        var root = NewRoot();
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "download")).FullName;
            var library = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
            await File.WriteAllTextAsync(Path.Combine(source, "01 - First.flac"), "first");

            var result = await CreateOrganizer().OrganizeAsync(AlbumJob(),
                new TransferItem("torrent", null, null, true), source, Preferences(library), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("expects 2 tracks", result.FailReason);
            Assert.Empty(Directory.EnumerateFiles(library, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Album_with_enough_but_unidentifiable_files_is_rejected_before_any_library_write()
    {
        var root = NewRoot();
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "download")).FullName;
            var library = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
            await File.WriteAllTextAsync(Path.Combine(source, "opaque-a.flac"), "wrong-a");
            await File.WriteAllTextAsync(Path.Combine(source, "opaque-b.flac"), "wrong-b");

            var result = await CreateOrganizer().OrganizeAsync(AlbumJob(),
                new TransferItem("torrent", null, null, true), source, Preferences(library), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("payload is ambiguous", result.FailReason);
            Assert.Empty(Directory.EnumerateFiles(library, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Music_completion_contract_requires_the_scope_specific_inventory()
    {
        var album = AlbumJob().Music!;
        Assert.True(album.HasCompletionContract);
        album.Tracks[0].Title = string.Empty;
        Assert.False(album.HasCompletionContract);
        album.Tracks.Clear();
        Assert.False(album.HasCompletionContract);

        var track = TrackJob().Music!;
        Assert.True(track.HasCompletionContract);
        track.Tracks[0].Title = string.Empty;
        Assert.False(track.HasCompletionContract);
        track.Tracks.Clear();
        Assert.False(track.HasCompletionContract);

        var artist = new MusicAcquisitionContextDto { Kind = MediaKind.Artist, Artist = "Artist" };
        Assert.False(artist.HasCompletionContract);
        artist.ExpectedAlbums.Add("Album");
        Assert.True(artist.HasCompletionContract);

        artist.ExpectedAlbums[0] = " ";
        Assert.False(artist.HasCompletionContract);

        // Legacy or manually edited durable JSON may contain explicit null collections despite the
        // non-nullable DTO shape. Evaluating that payload must request enrichment rather than crash.
        album.Tracks = null!;
        artist.ExpectedAlbums = null!;
        Assert.False(album.HasCompletionContract);
        Assert.False(artist.HasCompletionContract);
    }

    [Fact]
    public async Task Direct_album_keeps_short_valid_tracks_below_the_general_torrent_size_floor()
    {
        var root = NewRoot();
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "download")).FullName;
            var library = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
            await File.WriteAllTextAsync(Path.Combine(source, "01-01 - First [abcdefghijk].m4a"), "short-one");
            await File.WriteAllTextAsync(Path.Combine(source, "01-02 - Second [ZYXWVUT9876].m4a"), "short-two");
            var preferences = Preferences(library, minAudioFileSizeMb: 1);

            var result = await CreateOrganizer().OrganizeAsync(AlbumJob(),
                new TransferItem("direct", null, null, true, Protocol: AcquisitionProtocol.DirectAudio),
                source, preferences, CancellationToken.None);

            Assert.True(result.Success, result.FailReason);
            Assert.Equal(2, result.MediaFileCount);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Track_import_rejects_an_ambiguous_multi_file_payload()
    {
        var root = NewRoot();
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "download")).FullName;
            var library = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
            await File.WriteAllTextAsync(Path.Combine(source, "01 - Intro.flac"), "intro");
            await File.WriteAllTextAsync(Path.Combine(source, "02 - Other Song.flac"), "other");

            var result = await CreateOrganizer().OrganizeAsync(TrackJob(),
                new TransferItem("torrent", null, null, false), source, Preferences(library), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("payload is ambiguous", result.FailReason);
            Assert.Empty(Directory.EnumerateFiles(library, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Track_import_accepts_a_single_file_even_when_its_source_name_is_opaque()
    {
        var root = NewRoot();
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "download")).FullName;
            var library = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
            await File.WriteAllTextAsync(Path.Combine(source, "audio.flac"), "track");

            var result = await CreateOrganizer().OrganizeAsync(TrackJob(),
                new TransferItem("torrent", null, null, false), source, Preferences(library), CancellationToken.None);

            Assert.True(result.Success, result.FailReason);
            Assert.True(File.Exists(Path.Combine(library, "Artist", "Studio Album (2026)", "01-01 - Test Song.flac")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Plex_verification_matches_album_and_artist_after_normalization()
    {
        const string search = """
            {"MediaContainer":{"Hub":[{"type":"album","Metadata":[
              {"type":"album","ratingKey":"77","title":"Random Access Memories","parentTitle":"Daft Punk"}
            ]}]}}
            """;
        const string tracks = """
            {"MediaContainer":{"Metadata":[
              {"type":"track","title":"Give Life Back to Music"},
              {"type":"track","title":"Instant Crush"}
            ]}}
            """;
        var http = new HttpClient(new JsonHandler(search, tracks));
        var service = new PlexMusicService(http, Options.Create(new PlexConfiguration
        {
            PrimaryServerUrl = "http://plex:32400", ServerToken = "token"
        }), NullLogger<PlexMusicService>.Instance);

        var result = await service.VerifyAsync(new PlexVerificationRequest(
            MediaType.Music, MediaKind.Album, "musicbrainz", "unknown-release-group",
            "Daft-Punk", "Random Access Memories!", null, ExpectedTracks:
            ["Give Life Back to Music", "Instant Crush"]));

        Assert.True(result.Available);
        Assert.Equal("77", result.RatingKey);
    }

    [Fact]
    public async Task Album_verification_rejects_a_partial_album_with_a_duplicate_track_title()
    {
        const string search = """
            {"MediaContainer":{"Hub":[{"type":"album","Metadata":[
              {"type":"album","ratingKey":"77","title":"Album","parentTitle":"Artist"}
            ]}]}}
            """;
        const string partial = """
            {"MediaContainer":{"Metadata":[{"type":"track","title":"First"}]}}
            """;
        var service = new PlexMusicService(new HttpClient(new JsonHandler(search, partial)),
            Options.Create(new PlexConfiguration { PrimaryServerUrl = "http://plex:32400", ServerToken = "token" }),
            NullLogger<PlexMusicService>.Instance);

        var result = await service.VerifyAsync(new PlexVerificationRequest(
            MediaType.Music, MediaKind.Album, "youtube", "album-id",
            "Artist", "Album", null, ExpectedTracks: ["First", "First"]));

        Assert.False(result.Available);
        Assert.Equal("77", result.RatingKey);
        Assert.Contains("missing 1 of 2", result.Detail);
    }

    [Fact]
    public async Task Track_verification_skips_a_same_named_live_recording_and_matches_the_requested_album()
    {
        const string search = """
            {"MediaContainer":{"Hub":[{"type":"track","Metadata":[
              {"type":"track","ratingKey":"1","title":"Test Song","grandparentTitle":"Artist","parentTitle":"Live Album","duration":240000},
              {"type":"track","ratingKey":"2","title":"Test Song","grandparentTitle":"Artist","parentTitle":"Studio Album","duration":183000}
            ]}]}}
            """;
        var service = new PlexMusicService(new HttpClient(new JsonHandler(search)),
            Options.Create(new PlexConfiguration { PrimaryServerUrl = "http://plex:32400", ServerToken = "token" }),
            NullLogger<PlexMusicService>.Instance);

        var result = await service.VerifyAsync(new PlexVerificationRequest(
            MediaType.Music, MediaKind.Track, "youtube", "abcdefghijk",
            "Artist", "Studio Album", "Test Song", ExpectedDurationMs: 180_000));

        Assert.True(result.Available);
        Assert.Equal("2", result.RatingKey);
    }

    [Fact]
    public async Task Track_verification_fetches_full_metadata_when_the_search_hub_omits_contract_fields()
    {
        const string search = """
            {"MediaContainer":{"Hub":[{"type":"track","Metadata":[
              {"type":"track","ratingKey":"2","title":"Test Song"}
            ]}]}}
            """;
        const string detail = """
            {"MediaContainer":{"Metadata":[
              {"type":"track","ratingKey":"2","title":"Test Song","grandparentTitle":"Artist","parentTitle":"Studio Album","duration":183000}
            ]}}
            """;
        var service = new PlexMusicService(new HttpClient(new JsonHandler(search, detail)),
            Options.Create(new PlexConfiguration { PrimaryServerUrl = "http://plex:32400", ServerToken = "token" }),
            NullLogger<PlexMusicService>.Instance);

        var result = await service.VerifyAsync(new PlexVerificationRequest(
            MediaType.Music, MediaKind.Track, "youtube", "abcdefghijk",
            "Artist", "Studio Album", "Test Song", ExpectedDurationMs: 180_000));

        Assert.True(result.Available);
        Assert.Equal("2", result.RatingKey);
    }

    [Fact]
    public async Task Artist_verification_requires_every_expected_album_not_just_the_artist()
    {
        const string artist = """
            {"MediaContainer":{"Hub":[{"type":"artist","Metadata":[
              {"type":"artist","ratingKey":"9","title":"Artist"}
            ]}]}}
            """;
        const string albums = """
            {"MediaContainer":{"Metadata":[
              {"type":"album","ratingKey":"10","title":"First","parentTitle":"Artist"}
            ]}}
            """;
        var service = new PlexMusicService(new HttpClient(new JsonHandler(artist, albums)),
            Options.Create(new PlexConfiguration { PrimaryServerUrl = "http://plex:32400", ServerToken = "token" }),
            NullLogger<PlexMusicService>.Instance);

        var result = await service.VerifyAsync(new PlexVerificationRequest(
            MediaType.Music, MediaKind.Artist, "musicbrainz", "artist-id",
            "Artist", null, null, ["First", "Still Missing"]));

        Assert.False(result.Available);
        Assert.Contains("missing 1", result.Detail);
    }

    private static LibraryOrganizer CreateOrganizer() => new(
        new NoArchives(), new NoSeasonPacks(), new NoEpisodes(), new PlexNamingService(),
        new ReleaseParser(), NullLogger<LibraryOrganizer>.Instance);

    private static EffectiveLibraryOrganization Preferences(string library, double minAudioFileSizeMb = 0) => new()
    {
        MusicPath = library,
        TransferMode = TransferMode.Copy,
        MinAudioFileSizeMb = minAudioFileSizeMb
    };

    private static FulfillmentJobDto AlbumJob() => new()
    {
        Id = 1,
        Title = "Album",
        Year = 2026,
        MediaType = MediaType.Music,
        RequestScope = RequestScopeKind.Album,
        Music = new MusicAcquisitionContextDto
        {
            Kind = MediaKind.Album,
            Artist = "Artist",
            Album = "Album",
            TrackCount = 2,
            Tracks =
            [
                new() { RecordingId = "1", Title = "First", DiscNumber = 1, TrackNumber = 1 },
                new() { RecordingId = "2", Title = "Second", DiscNumber = 1, TrackNumber = 2 }
            ]
        }
    };

    private static FulfillmentJobDto TrackJob() => new()
    {
        Id = 2,
        Title = "Test Song",
        Year = 2026,
        MediaType = MediaType.Music,
        RequestScope = RequestScopeKind.Track,
        Music = new MusicAcquisitionContextDto
        {
            Kind = MediaKind.Track,
            Artist = "Artist",
            Album = "Studio Album",
            Track = "Test Song",
            TrackCount = 1,
            Tracks =
            [
                new() { RecordingId = "track", Title = "Test Song", DiscNumber = 1, TrackNumber = 1 }
            ]
        }
    };

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plexrequests-music-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class NoArchives : IArchiveExtractor
    {
        public bool LooksLikeArchive(string filePath) => false;
        public bool IsContinuationVolume(string filePath) => false;
        public Task ExtractAsync(string archiveFilePath, string destinationDirectory, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class NoSeasonPacks : ISeasonPackSplitter
    {
        public IReadOnlyList<(string FilePath, int Episode)> Map(IReadOnlyList<string> videoFiles, int season,
            int? expectedEpisodeCount) => Array.Empty<(string, int)>();
    }

    private sealed class NoEpisodes : IEpisodeTitleProvider
    {
        public Task<IReadOnlyList<EpisodeDto>> GetSeasonEpisodesAsync(int? tmdbId, int season, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EpisodeDto>>(Array.Empty<EpisodeDto>());
        public Task<string?> GetEpisodeTitleAsync(int? tmdbId, int season, int episode, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }

    private sealed class JsonHandler(params string[] json) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(json);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), System.Text.Encoding.UTF8, "application/json")
            });
    }
}
