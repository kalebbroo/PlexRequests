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
                new TorrentItem("torrent", null, null, true), source, Preferences(library), CancellationToken.None);

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
                new TorrentItem("torrent", null, null, true), source, Preferences(library), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("expects 2 tracks", result.FailReason);
            Assert.Empty(Directory.EnumerateFiles(library, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Plex_verification_matches_album_and_artist_after_normalization()
    {
        const string json = """
            {"MediaContainer":{"Hub":[{"type":"album","Metadata":[
              {"type":"album","ratingKey":"77","title":"Random Access Memories","parentTitle":"Daft Punk"}
            ]}]}}
            """;
        var http = new HttpClient(new JsonHandler(json));
        var service = new PlexMusicService(http, Options.Create(new PlexConfiguration
        {
            PrimaryServerUrl = "http://plex:32400", ServerToken = "token"
        }), NullLogger<PlexMusicService>.Instance);

        var result = await service.VerifyAsync(new PlexVerificationRequest(
            MediaType.Music, MediaKind.Album, "musicbrainz", "unknown-release-group",
            "Daft-Punk", "Random Access Memories!", null));

        Assert.True(result.Available);
        Assert.Equal("77", result.RatingKey);
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

    private static EffectiveLibraryOrganization Preferences(string library) => new()
    {
        MusicPath = library,
        TransferMode = TransferMode.Copy,
        MinAudioFileSizeMb = 0
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
