using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Download;
using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

public sealed class DirectAudioAcquisitionTests
{
    [Fact]
    public void Locator_round_trips_an_exact_versioned_tracklist_and_rejects_bad_ids()
    {
        var input = new YouTubeMusicLocator(1,
        [
            new YouTubeMusicTrack("abcdefghijk", "First", 1, 1),
            new YouTubeMusicTrack("ZYXWVUT9876", "Second", 1, 2)
        ]);

        Assert.True(YouTubeMusicLocator.TryDecode(input.Encode(), out var decoded));
        Assert.Equal(input.Tracks, decoded.Tracks);
        Assert.False(YouTubeMusicLocator.TryDecode(
            new YouTubeMusicLocator(1, [new YouTubeMusicTrack("not-valid", "Bad", 1, 1)]).Encode(), out _));

        var enriched = new YouTubeMusicLocator(2,
            [new YouTubeMusicTrack("abcdefghijk", "First", 1, 1, "Artist", "Album", 1, 2026)],
            "https://lh3.googleusercontent.com/cover");
        Assert.True(YouTubeMusicLocator.TryDecode(enriched.Encode(), out var decodedV2));
        Assert.Equal(2, decodedV2.Version);
        Assert.Equal("Artist", decodedV2.Tracks[0].Artist);
        Assert.Equal(enriched.ArtworkUrl, decodedV2.ArtworkUrl);
    }

    [Fact]
    public async Task Source_offers_one_authoritative_album_candidate_only_when_admin_and_provider_allow_it()
    {
        var source = new YouTubeMusicDirectSource(Options.Create(new DirectAudioOptions { Enabled = true }));
        var job = AlbumJob();

        var candidate = Assert.Single(await source.SearchAsync(job, CancellationToken.None));
        Assert.Equal(AcquisitionProtocol.DirectAudio, candidate.Acquisition.Protocol);
        Assert.Equal("YouTube Music direct", candidate.Source);
        Assert.True(YouTubeMusicLocator.TryDecode(candidate.Acquisition.Locator, out var locator));
        Assert.Equal(2, locator.Version);
        Assert.Equal(new[] { "abcdefghijk", "ZYXWVUT9876" }, locator.Tracks.Select(x => x.VideoId));
        Assert.All(locator.Tracks, x =>
        {
            Assert.Equal("Test Artist", x.Artist);
            Assert.Equal("Test Album", x.Album);
            Assert.Equal(2026, x.Year);
            Assert.Equal(2, x.TrackCount);
            Assert.Equal(1, x.DiscCount);
            Assert.Equal("Test Artist", x.AlbumArtist);
        });
        Assert.Equal(job.Music!.ArtworkUrl, locator.ArtworkUrl);

        job.AllowedAcquisitionProtocols = [AcquisitionProtocol.Torrent];
        Assert.False(source.AppliesTo(job));
        Assert.Empty(await source.SearchAsync(job, CancellationToken.None));
    }

    [Fact]
    public async Task Failed_direct_resource_is_protocol_blocklisted_so_torrent_fallback_can_win_later()
    {
        var source = new YouTubeMusicDirectSource(Options.Create(new DirectAudioOptions { Enabled = true }));
        var job = AlbumJob();
        var candidate = Assert.Single(await source.SearchAsync(job, CancellationToken.None));
        var sourceId = Assert.IsType<string>(candidate.Acquisition.SourceId);
        var context = TestData.Context(blocklist: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AcquisitionResource.BlocklistKey(AcquisitionProtocol.DirectAudio, sourceId)
        });

        var ranked = new ReleaseEvaluator(new ReleaseParser()).Evaluate(candidate, job, context);

        Assert.False(ranked.Accepted);
        Assert.Contains(ranked.Rejections, x => x.Reason == RejectionReason.Blocklisted);
    }

    [Fact]
    public async Task MusicBrainz_album_can_use_a_strictly_equivalent_YouTube_manifest()
    {
        var canonical = AlbumDetail("musicbrainz", "release-group", "mb-one", "mb-two");
        var youtube = AlbumDetail("youtube", "MPRE-equivalent", "abcdefghijk", "ZYXWVUT9876");
        var metadata = new MappingMetadataProvider(youtube);
        var resolver = new YouTubeMusicDirectAcquisitionResolver(metadata,
            NullLogger<YouTubeMusicDirectAcquisitionResolver>.Instance);

        var direct = await resolver.ResolveAsync(canonical.ResolveMediaRef(), canonical);

        Assert.NotNull(direct);
        Assert.Equal("youtube", direct.Provider);
        Assert.Equal("MPRE-equivalent", direct.ExternalId);
        Assert.Equal(new[] { "abcdefghijk", "ZYXWVUT9876" }, direct.Tracks.Select(x => x.RecordingId));
        Assert.Equal(1, metadata.Searches);
        Assert.Equal(1, metadata.DetailReads);

        var job = AlbumJob();
        job.Media = canonical.ResolveMediaRef();
        job.Music!.DirectAudio = direct;
        var source = new YouTubeMusicDirectSource(Options.Create(new DirectAudioOptions { Enabled = true }));
        var candidate = Assert.Single(await source.SearchAsync(job, CancellationToken.None));
        Assert.Equal("youtube:album:MPRE-equivalent", candidate.Acquisition.SourceId);
        Assert.True(YouTubeMusicLocator.TryDecode(candidate.Acquisition.Locator, out var locator));
        Assert.Equal(direct.Tracks.Select(x => x.RecordingId), locator.Tracks.Select(x => x.VideoId));
    }

    [Fact]
    public async Task Cross_catalog_mapping_rejects_an_incomplete_or_different_album()
    {
        var canonical = AlbumDetail("musicbrainz", "release-group", "mb-one", "mb-two");
        var different = AlbumDetail("youtube", "MPRE-wrong", "abcdefghijk", "ZYXWVUT9876");
        different.Music!.Tracks[1].Title = "Live Bonus";
        var metadata = new MappingMetadataProvider(different);
        var resolver = new YouTubeMusicDirectAcquisitionResolver(metadata,
            NullLogger<YouTubeMusicDirectAcquisitionResolver>.Instance);

        var direct = await resolver.ResolveAsync(canonical.ResolveMediaRef(), canonical);

        Assert.Null(direct);
    }

    [Fact]
    public async Task Cross_catalog_track_mapping_rejects_a_different_album_or_recording_length()
    {
        var canonical = TrackDetail("musicbrainz", "recording-id", "mb-track", "Studio Album", 180_000);
        var live = TrackDetail("youtube", "abcdefghijk", "abcdefghijk", "Live Album", 240_000);
        var metadata = new MappingMetadataProvider(live);
        var resolver = new YouTubeMusicDirectAcquisitionResolver(metadata,
            NullLogger<YouTubeMusicDirectAcquisitionResolver>.Instance);

        var direct = await resolver.ResolveAsync(canonical.ResolveMediaRef(), canonical);

        Assert.Null(direct);
    }

    [Fact]
    public async Task Backend_persists_completed_audio_and_a_new_instance_can_reconcile_it()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"plexrequests-direct-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "fake-yt-dlp.sh");
            await File.WriteAllTextAsync(executable, """
                #!/bin/sh
                out=""
                while [ "$#" -gt 0 ]; do
                  if [ "$1" = "--output" ]; then shift; out="$1"; fi
                  shift
                done
                target=$(printf '%s' "$out" | sed 's/%(ext)s/m4a/g')
                dd if=/dev/zero of="$target" bs=1024 count=64 2>/dev/null
                printf 'download:100.0%%\n'
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var options = Options.Create(new DirectAudioOptions
            {
                StatePath = Path.Combine(root, "state"),
                ExecutablePath = executable,
                MaxAttempts = 1,
                TrackTimeoutMinutes = 2
            });
            var lifetime = new TestLifetime();
            var enricher = new RecordingEnricher();
            var backend = new YouTubeMusicAcquisitionBackend(options, lifetime, enricher,
                NullLogger<YouTubeMusicAcquisitionBackend>.Instance);
            var resource = AcquisitionResource.DirectAudio(new YouTubeMusicLocator(1,
                [new YouTubeMusicTrack("abcdefghijk", "Test Track", 1, 1)]).Encode(), "youtube:track:abcdefghijk");

            var id = await backend.EnqueueAsync(new AcquisitionRequest(resource, null, "Test Track", "job-7"),
                CancellationToken.None);
            Assert.NotNull(id);
            TransferStatus? status = null;
            for (var i = 0; i < 100 && status?.IsFinished != true; i++)
            {
                await Task.Delay(25);
                status = await backend.GetStatusAsync(id!, CancellationToken.None);
            }
            Assert.NotNull(status);
            Assert.True(status.IsFinished, status.ProviderStatus);
            Assert.Single(status.Files);
            Assert.True(status.TotalSizeBytes >= 64 * 1024);
            Assert.Single(enricher.Tagged);
            Assert.Equal("Test Track", enricher.Tagged[0].Track.Title);

            var afterRestart = new YouTubeMusicAcquisitionBackend(options, lifetime, enricher,
                NullLogger<YouTubeMusicAcquisitionBackend>.Instance);
            var recovered = await afterRestart.GetStatusAsync(id!, CancellationToken.None);
            Assert.NotNull(recovered);
            Assert.True(recovered.IsFinished);
            Assert.Equal(TransferVerdict.Import,
                afterRestart.EvaluateHealth(recovered, DateTime.UtcNow, null, DateTime.UtcNow).Verdict);
            Assert.True(await afterRestart.RemoveAsync(id!, removeData: true, CancellationToken.None));
            Assert.Null(await afterRestart.GetStatusAsync(id!, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static FulfillmentJobDto AlbumJob() => new()
    {
        Id = 7,
        MediaType = MediaType.Music,
        Media = MediaRef.FromExternal("youtube", "MPRE-album", MediaType.Music, MediaKind.Album),
        RequestScope = RequestScopeKind.Album,
        Title = "Test Album",
        Year = 2026,
        AllowedAcquisitionProtocols = [AcquisitionProtocol.Torrent, AcquisitionProtocol.DirectAudio],
        Music = new MusicAcquisitionContextDto
        {
            Kind = MediaKind.Album,
            Artist = "Test Artist",
            Album = "Test Album",
            ArtworkUrl = "https://lh3.googleusercontent.com/test-cover",
            TrackCount = 2,
            Tracks =
            [
                new MusicTrackMetadataDto { RecordingId = "abcdefghijk", Title = "First", DiscNumber = 1, TrackNumber = 1 },
                new MusicTrackMetadataDto { RecordingId = "ZYXWVUT9876", Title = "Second", DiscNumber = 1, TrackNumber = 2 }
            ]
        }
    };

    private static MediaDetailDto AlbumDetail(string provider, string id, string firstId, string secondId) => new()
    {
        Title = "Test Album",
        MediaType = MediaType.Music,
        ExternalSource = provider,
        ExternalId = id,
        MediaRef = MediaRef.FromExternal(provider, id, MediaType.Music, MediaKind.Album),
        Music = new MusicMetadataDto
        {
            Kind = MediaKind.Album,
            ArtistCredit = "Test Artist",
            TrackCount = 2,
            Tracks =
            [
                new MusicTrackMetadataDto
                {
                    RecordingId = firstId, Title = "First", ArtistCredit = "Test Artist",
                    DiscNumber = 1, TrackNumber = 1, DurationMs = 180_000
                },
                new MusicTrackMetadataDto
                {
                    RecordingId = secondId, Title = "Second", ArtistCredit = "Test Artist",
                    DiscNumber = 1, TrackNumber = 2, DurationMs = 200_000
                }
            ]
        }
    };

    private static MediaDetailDto TrackDetail(string provider, string id, string recordingId,
        string album, int durationMs) => new()
    {
        Title = "Test Track",
        MediaType = MediaType.Music,
        ExternalSource = provider,
        ExternalId = id,
        MediaRef = MediaRef.FromExternal(provider, id, MediaType.Music, MediaKind.Track),
        Music = new MusicMetadataDto
        {
            Kind = MediaKind.Track,
            ArtistCredit = "Test Artist",
            AlbumTitle = album,
            TrackCount = 1,
            Tracks =
            [
                new MusicTrackMetadataDto
                {
                    RecordingId = recordingId, Title = "Test Track", ArtistCredit = "Test Artist",
                    DiscNumber = 1, TrackNumber = 1, DurationMs = durationMs
                }
            ]
        }
    };

    private sealed class MappingMetadataProvider(MediaDetailDto youtube) : IMediaMetadataProvider
    {
        public int Searches { get; private set; }
        public int DetailReads { get; private set; }

        public Task<List<MediaCardDto>> SearchAsync(MediaSearchQuery query)
        {
            Searches++;
            return Task.FromResult(new List<MediaCardDto>
            {
                new()
                {
                    Title = youtube.Title,
                    MediaType = MediaType.Music,
                    ExternalSource = "youtube",
                    ExternalId = youtube.ExternalId,
                    MediaRef = youtube.ResolveMediaRef()
                }
            });
        }

        public Task<MediaDetailDto?> GetDetailsAsync(MediaRef mediaRef)
        {
            DetailReads++;
            return Task.FromResult<MediaDetailDto?>(youtube);
        }

        public Task<List<MediaCardDto>> SearchAsync(string query, MediaType? mediaType = null,
            int page = 1, int pageSize = 20) => Task.FromResult(new List<MediaCardDto>());
        public Task<MediaDetailDto?> GetDetailsAsync(int mediaId, MediaType mediaType) => Task.FromResult<MediaDetailDto?>(null);
        public Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10) => Task.FromResult(new List<MediaCardDto>());
        public Task<List<MediaCardDto>> GetLibraryAsync(MediaType mediaType, int page = 1, int pageSize = 20) =>
            Task.FromResult(new List<MediaCardDto>());
        public Task<string?> GetImdbIdAsync(int mediaId, MediaType mediaType) => Task.FromResult<string?>(null);
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public void StopApplication() => _stopping.Cancel();
    }

    private sealed class RecordingEnricher : IDirectAudioMediaEnricher
    {
        public List<(string Path, YouTubeMusicTrack Track)> Tagged { get; } = new();
        public Task<string?> FetchArtworkAsync(string? artworkUrl, string payloadPath, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task WriteTagsAsync(string audioPath, YouTubeMusicTrack track, string? artworkPath,
            CancellationToken ct)
        {
            Tagged.Add((audioPath, track));
            return Task.CompletedTask;
        }
    }
}
