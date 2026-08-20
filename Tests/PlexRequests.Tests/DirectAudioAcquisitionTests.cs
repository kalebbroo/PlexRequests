using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Download;
using PlexRequests.Downloader.Indexers;
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
        Assert.Equal(new[] { "abcdefghijk", "ZYXWVUT9876" }, locator.Tracks.Select(x => x.VideoId));

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
            var backend = new YouTubeMusicAcquisitionBackend(options, lifetime,
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

            var afterRestart = new YouTubeMusicAcquisitionBackend(options, lifetime,
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
        AllowedAcquisitionProtocols = [AcquisitionProtocol.Torrent, AcquisitionProtocol.DirectAudio],
        Music = new MusicAcquisitionContextDto
        {
            Kind = MediaKind.Album,
            Artist = "Test Artist",
            Album = "Test Album",
            TrackCount = 2,
            Tracks =
            [
                new MusicTrackMetadataDto { RecordingId = "abcdefghijk", Title = "First", DiscNumber = 1, TrackNumber = 1 },
                new MusicTrackMetadataDto { RecordingId = "ZYXWVUT9876", Title = "Second", DiscNumber = 1, TrackNumber = 2 }
            ]
        }
    };

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
}
