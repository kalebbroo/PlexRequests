using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
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

public sealed class DirectVideoAcquisitionTests
{
    private const string VideoId = "abcdefghijk";

    [Fact]
    public void Locator_round_trips_and_rejects_malformed_identity()
    {
        var input = new DirectVideoLocator(1, VideoId, 3, 1, "The Friendly Neighborhood", "UCAAAAAAAAAAAAAAAAAAAAAA");

        Assert.True(DirectVideoLocator.TryDecode(input.Encode(), out var decoded));
        Assert.Equal(input, decoded);
        Assert.False(DirectVideoLocator.TryDecode(new DirectVideoLocator(1, "short", 3, 1, "T").Encode(), out _));
        Assert.False(DirectVideoLocator.TryDecode(new DirectVideoLocator(1, VideoId, 0, 1, "T").Encode(), out _));
        Assert.False(DirectVideoLocator.TryDecode("ytmusic:v2:AAAA", out _));
    }

    [Fact]
    public void Evaluator_admits_only_the_frozen_episode_the_job_still_wants_and_ranks_it_below_torrents()
    {
        var job = TvJob(requested: [(3, 1)]);
        var candidate = Candidate(3, 1);

        var accepted = DirectVideoReleaseEvaluator.Evaluate(candidate, job, Context, new ParsedRelease());
        Assert.True(accepted.Accepted);
        Assert.Equal(DirectVideoReleaseEvaluator.FallbackScore, accepted.Score);
        Assert.Equal(3, accepted.Season);
        Assert.Equal(1, accepted.Episode);

        var notWanted = DirectVideoReleaseEvaluator.Evaluate(Candidate(3, 2), job, Context, new ParsedRelease());
        Assert.False(notWanted.Accepted);
        Assert.Contains(notWanted.Rejections, x => x.Reason == RejectionReason.WrongEpisode);
    }

    [Fact]
    public void Planner_takes_an_acceptable_torrent_over_a_direct_copy_even_when_the_torrent_score_is_very_negative()
    {
        var job = TvJob(requested: [(3, 1)]);
        var direct = new RankedCandidate
        {
            Candidate = Candidate(3, 1), Parsed = new ParsedRelease(), Accepted = true,
            Season = 3, Episode = 1, CanonicalEpisodeCoverage = [new EpisodeRef { Season = 3, Episode = 1 }],
            Score = DirectVideoReleaseEvaluator.FallbackScore
        };
        var torrent = new RankedCandidate
        {
            Candidate = new ReleaseCandidate
            {
                ReleaseName = "Marvels Spidey S03E01 1080p WEB",
                Acquisition = AcquisitionResource.Torrent("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567")
            },
            Parsed = new ParsedRelease(), Accepted = true, Season = 3, Episode = 1,
            CanonicalEpisodeCoverage = [new EpisodeRef { Season = 3, Episode = 1 }],
            Score = -50_000
        };

        var result = new DownloadPlanner().Plan([direct, torrent], job,
            new RankingContext { Preferences = new DownloadPreferencesDto() });

        var item = Assert.Single(result.Plan.Items);
        Assert.Equal(torrent.Candidate.ReleaseName, item.Candidate.ReleaseName);
    }

    [Fact]
    public void Evaluator_rejects_non_tv_jobs()
    {
        var job = TvJob(requested: [(3, 1)]);
        job.MediaType = MediaType.Movie;

        Assert.False(DirectVideoReleaseEvaluator.Evaluate(Candidate(3, 1), job, Context, new ParsedRelease()).Accepted);
    }

    [Fact]
    public void Candidate_source_emits_one_candidate_per_outstanding_frozen_episode_only()
    {
        var job = TvJob(requested: [(3, 1)]);
        job.DirectVideo = new DirectVideoAcquisitionContextDto
        {
            Episodes =
            [
                new DirectVideoEpisodeDto { Season = 3, Episode = 1, VideoId = VideoId, Title = "The Friendly Neighborhood", ChannelId = "UCAAAAAAAAAAAAAAAAAAAAAA", DurationSeconds = 660 },
                new DirectVideoEpisodeDto { Season = 3, Episode = 2, VideoId = "ZYXWVUT9876", Title = "Other Episode", ChannelId = "UCAAAAAAAAAAAAAAAAAAAAAA", DurationSeconds = 660 }
            ]
        };
        var source = new YouTubeVideoDirectSource(Options.Create(new DirectVideoOptions()));

        var candidates = source.SearchAsync(job, CancellationToken.None).Result;

        var only = Assert.Single(candidates);
        Assert.Equal(AcquisitionProtocol.DirectVideo, only.Acquisition.Protocol);
        Assert.True(DirectVideoLocator.TryDecode(only.Acquisition.Locator, out var locator));
        Assert.Equal(VideoId, locator.VideoId);
    }

    [Fact]
    public async Task Catalog_reads_uploads_once_per_cache_window_and_sends_the_key_only_to_the_api()
    {
        var handler = new FakeYouTubeHandler(uri => uri.AbsolutePath switch
        {
            "/youtube/v3/channels" => """{"items":[{"contentDetails":{"relatedPlaylists":{"uploads":"UUAAAA"}}}]}""",
            "/youtube/v3/playlistItems" => """{"items":[{"contentDetails":{"videoId":"abcdefghijk"}}]}""",
            "/youtube/v3/videos" => """{"items":[{"id":"abcdefghijk","snippet":{"title":"Marvel's Spidey and His Amazing Friends S3E1","channelId":"UCAAAAAAAAAAAAAAAAAAAAAA","liveBroadcastContent":"none"},"contentDetails":{"duration":"PT11M"}}]}""",
            _ => throw new InvalidOperationException(uri.AbsolutePath)
        });
        var catalog = Catalog(handler, apiKey: "test-key");

        var first = await catalog.GetChannelUploadsAsync("UCAAAAAAAAAAAAAAAAAAAAAA");
        var second = await catalog.GetChannelUploadsAsync("UCAAAAAAAAAAAAAAAAAAAAAA");

        var video = Assert.Single(first!);
        Assert.Equal(660, video.DurationSeconds);
        Assert.Equal(first, second);
        Assert.Equal(3, handler.Calls);
        Assert.All(handler.Urls, url => Assert.DoesNotContain("test-key", url));
        Assert.All(handler.ApiKeys, key => Assert.Equal("test-key", key));
    }

    [Fact]
    public async Task Catalog_without_a_key_makes_no_request_and_reports_unavailable()
    {
        var handler = new FakeYouTubeHandler(_ => "{}");
        var catalog = Catalog(handler, apiKey: string.Empty);

        Assert.Null(await catalog.GetChannelUploadsAsync("UCAAAAAAAAAAAAAAAAAAAAAA"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Backend_downloads_the_frozen_video_and_reports_it_finished()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"plexrequests-video-{Guid.NewGuid():N}");
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
                target=$(printf '%s' "$out" | sed 's/%(ext)s/mp4/g')
                dd if=/dev/zero of="$target" bs=1024 count=128 2>/dev/null
                printf 'download:100.0%%\n'
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var options = Options.Create(new DirectVideoOptions
            {
                StatePath = Path.Combine(root, "state"),
                ExecutablePath = executable,
                MaxAttempts = 1,
                TimeoutMinutes = 2
            });
            var backend = new YouTubeVideoAcquisitionBackend(options, new TestLifetime(),
                NullLogger<YouTubeVideoAcquisitionBackend>.Instance);
            var resource = AcquisitionResource.DirectVideo(
                new DirectVideoLocator(1, VideoId, 3, 1, "The Friendly Neighborhood").Encode(), $"youtube:video:{VideoId}");

            var id = await backend.EnqueueAsync(new AcquisitionRequest(resource, null, "Show S03E01", "job-9"),
                CancellationToken.None);
            Assert.NotNull(id);
            TransferStatus? status = null;
            for (var i = 0; i < 200 && status?.IsFinished != true; i++)
            {
                await Task.Delay(25);
                status = await backend.GetStatusAsync(id!, CancellationToken.None);
            }

            Assert.NotNull(status);
            Assert.True(status.IsFinished, status.ProviderStatus);
            Assert.Contains(status.Files, file => file.Contains($"[{VideoId}]", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static YouTubeDataVideoCatalog Catalog(FakeYouTubeHandler handler, string apiKey)
    {
        var http = new HttpClient(handler);
        var options = Options.Create(new YouTubeDataOptions
        {
            ApiKey = apiKey,
            BaseUrl = "https://example.test/youtube/v3/"
        });
        return new YouTubeDataVideoCatalog(http, options, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<YouTubeDataVideoCatalog>.Instance);
    }

    private static RankingContext Context => new() { Preferences = new DownloadPreferencesDto() };

    [Fact]
    public void Evaluator_refuses_blocklisted_videos_and_jobs_above_the_fallback_quality()
    {
        var job = TvJob(requested: [(3, 1)]);
        var blocked = new RankingContext
        {
            Preferences = new DownloadPreferencesDto(),
            BlocklistedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                AcquisitionResource.BlocklistKey(AcquisitionProtocol.DirectVideo, $"youtube:video:{VideoId}")
            }
        };
        Assert.False(DirectVideoReleaseEvaluator.Evaluate(Candidate(3, 1), job, blocked, new ParsedRelease()).Accepted);

        job.Quality = Quality.FullHD;
        Assert.False(DirectVideoReleaseEvaluator.Evaluate(Candidate(3, 1), job, Context, new ParsedRelease()).Accepted);
    }

    private static FulfillmentJobDto TvJob(IReadOnlyList<(int Season, int Episode)> requested) => new()
    {
        Id = 9,
        MediaType = MediaType.TvShow,
        Media = MediaRef.FromExternal("tmdb", "123", MediaType.TvShow, MediaKind.Series),
        Title = "Marvel's Spidey and His Amazing Friends",
        AllowedAcquisitionProtocols = [AcquisitionProtocol.Torrent, AcquisitionProtocol.DirectVideo],
        RequestedEpisodes = requested.Select(x => new EpisodeRef { Season = x.Season, Episode = x.Episode }).ToList()
    };

    private static ReleaseCandidate Candidate(int season, int episode) => new()
    {
        ReleaseName = $"Marvel's Spidey S{season:D2}E{episode:D2} [YouTube]",
        Acquisition = AcquisitionResource.DirectVideo(
            new DirectVideoLocator(1, VideoId, 3, 1, "The Friendly Neighborhood").Encode(), $"youtube:video:{VideoId}"),
        Source = "YouTube video direct",
        Season = season,
        Episode = episode,
        SeedersKnown = false,
        SizeKnown = false
    };

    private sealed class FakeYouTubeHandler(Func<Uri, string> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Urls { get; } = new();
        public List<string> ApiKeys { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Urls.Add(request.RequestUri!.ToString());
            ApiKeys.Add(request.Headers.TryGetValues("X-Goog-Api-Key", out var keys) ? keys.Single() : string.Empty);
            var body = respond(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
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
}
