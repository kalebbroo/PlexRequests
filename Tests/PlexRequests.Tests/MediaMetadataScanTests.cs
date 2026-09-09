using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

public sealed class MediaMetadataScanTests
{
    [Fact]
    public async Task QueueClaimsOnlyLatestCurrentUnknownFilesAndPersistsObservedMetadata()
    {
        await using var fixture = await Fixture.CreateAsync();
        var available = await fixture.AddTitleAsync(1, "Legacy movie");
        var busy = await fixture.AddTitleAsync(2, "Busy movie");
        var old = Video(available.Job.Id, "/movies/legacy.mkv", null, daysAgo: 2);
        var latest = Video(available.Job.Id, "/movies/legacy.mkv", null, daysAgo: 1);
        var knownFromName = Video(available.Job.Id, "/movies/known.mkv",
            "Known.Movie.2025.1080p.x265-GROUP");
        var busyFile = Video(busy.Job.Id, "/movies/busy.mkv", null);
        fixture.Db.ImportedFiles.AddRange(old, latest, knownFromName, busyFile);
        fixture.Db.FulfillmentJobs.Add(new FulfillmentJobEntity
        {
            MediaRequestId = busy.Request.Id,
            MediaId = busy.Request.MediaId,
            MediaType = MediaType.Movie,
            Title = busy.Request.Title,
            Status = FulfillmentStatus.Downloading
        });
        await fixture.Db.SaveChangesAsync();

        var queued = await fixture.Service.QueueAsync(new MediaMetadataScanRequestDto
        {
            RequestIds = [available.Request.Id, busy.Request.Id]
        });

        Assert.True(queued.Success, queued.Message);
        Assert.Equal(1, queued.QueuedCount);
        Assert.Equal(1, queued.BusyCount);
        Assert.Equal(MediaMetadataScanStatus.None, old.MediaMetadataScanStatus);
        Assert.Equal(MediaMetadataScanStatus.Queued, latest.MediaMetadataScanStatus);
        Assert.Equal(MediaMetadataScanStatus.None, knownFromName.MediaMetadataScanStatus);

        var claim = Assert.IsType<MediaMetadataScanTaskDto>(
            await fixture.Service.ClaimAsync("worker-a", CancellationToken.None));
        Assert.Equal(latest.Id, claim.ImportedFileId);
        Assert.Equal("/movies", claim.LibraryDestinationRootPath);
        Assert.Null(await fixture.Service.ClaimAsync("worker-b", CancellationToken.None));

        fixture.Db.ChangeTracker.Clear();
        Assert.False(await fixture.Service.ReportAsync(new MediaMetadataScanReportDto
        {
            ImportedFileId = claim.ImportedFileId,
            WorkerId = "not-the-owner",
            Succeeded = true,
            MediaTracks = Tracks("AVC")
        }, CancellationToken.None));
        Assert.True(await fixture.Service.ReportAsync(new MediaMetadataScanReportDto
        {
            ImportedFileId = claim.ImportedFileId,
            WorkerId = "worker-a",
            Succeeded = true,
            MediaTracks = Tracks("AVC")
        }, CancellationToken.None));

        fixture.Db.ChangeTracker.Clear();
        var scanned = await fixture.Db.ImportedFiles.SingleAsync(file => file.Id == latest.Id);
        Assert.Equal(MediaMetadataScanStatus.Succeeded, scanned.MediaMetadataScanStatus);
        Assert.Equal(1, scanned.MediaMetadataScanAttempts);
        Assert.Equal(1080, scanned.ResolutionHeight);
        Assert.NotNull(scanned.MediaMetadataScanCompletedAt);
        var tracks = JsonSerializer.Deserialize<MediaTrackSummaryDto>(scanned.MediaTracksJson!);
        Assert.Equal("AVC", Assert.Single(tracks!.Video).Codec);
    }

    [Fact]
    public async Task FailedInspectionIsVisibleAndCanBeExplicitlyRetried()
    {
        await using var fixture = await Fixture.CreateAsync();
        var title = await fixture.AddTitleAsync(3, "Missing movie");
        var file = Video(title.Job.Id, "/movies/missing.mp4", null);
        fixture.Db.ImportedFiles.Add(file);
        await fixture.Db.SaveChangesAsync();
        await fixture.Service.QueueAsync(new MediaMetadataScanRequestDto { RequestIds = [title.Request.Id] });
        var claim = Assert.IsType<MediaMetadataScanTaskDto>(
            await fixture.Service.ClaimAsync("worker", CancellationToken.None));
        fixture.Db.ChangeTracker.Clear();

        Assert.True(await fixture.Service.ReportAsync(new MediaMetadataScanReportDto
        {
            ImportedFileId = claim.ImportedFileId,
            WorkerId = "worker",
            Succeeded = false,
            Detail = "The audited library file is no longer present"
        }, CancellationToken.None));
        fixture.Db.ChangeTracker.Clear();
        var failed = await fixture.Db.ImportedFiles.SingleAsync(item => item.Id == file.Id);
        Assert.Equal(MediaMetadataScanStatus.Failed, failed.MediaMetadataScanStatus);
        Assert.Contains("no longer present", failed.MediaMetadataScanDetail);

        fixture.Db.ChangeTracker.Clear();
        var retried = await fixture.Service.QueueAsync(new MediaMetadataScanRequestDto
        {
            RequestIds = [title.Request.Id]
        });
        Assert.True(retried.Success);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(MediaMetadataScanStatus.Queued,
            (await fixture.Db.ImportedFiles.SingleAsync(item => item.Id == file.Id)).MediaMetadataScanStatus);
    }

    [Fact]
    public void ReadOnlyScanPathsAllowVideoFormatsButNeverEscapeConfiguredLibraryRoots()
    {
        var preferences = new EffectiveLibraryOrganization
        {
            MoviePath = "/library/movies",
            TvPath = "/library/tv"
        };
        var task = new LibraryAuditPathContext("/library/movies/Movie/movie.mp4", "/library/movies",
            MediaType.Movie, Quality.FullHD, [], false);

        var resolved = LibraryAuditPathResolver.Resolve(task, preferences);

        Assert.Equal(Path.GetFullPath("/library/movies/Movie/movie.mp4"), resolved.Path);
        Assert.Throws<RetiredLibraryPathException>(() => LibraryAuditPathResolver.Resolve(task with
        {
            DestinationPath = "/downloads/payload.mkv"
        }, preferences));
        Assert.Throws<InvalidOperationException>(() => LibraryAuditPathResolver.Resolve(task with
        {
            DestinationPath = "../outside/movie.mp4"
        }, preferences));
    }

    private static ImportedFileEntity Video(int jobId, string path, string? releaseName,
        int daysAgo = 0) => new()
    {
        FulfillmentJobId = jobId,
        DestinationPath = path,
        SourcePath = "/downloads/source.mkv",
        FileType = "video",
        SizeBytes = 1_000_000,
        ReleaseName = releaseName,
        ImportedAt = DateTime.UtcNow.AddDays(-daysAgo)
    };

    private static MediaTrackSummaryDto Tracks(string codec) => new()
    {
        HasVideo = true,
        Video =
        [
            new MediaTrackDto
            {
                Index = 1,
                Type = "Video",
                Codec = codec,
                Width = 1920,
                Height = 800
            }
        ]
    };

    private sealed class Fixture(SqliteConnection connection, AppDbContext db) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public MediaMetadataScanService Service { get; } = new(db, new ReleaseParser(),
            NullLogger<MediaMetadataScanService>.Instance);

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async Task<(MediaRequestEntity Request, FulfillmentJobEntity Job)> AddTitleAsync(
            int mediaId, string title)
        {
            var request = new MediaRequestEntity
            {
                MediaId = mediaId,
                MediaType = MediaType.Movie,
                Title = title,
                Status = RequestStatus.Available
            };
            Db.MediaRequests.Add(request);
            await Db.SaveChangesAsync();
            var job = new FulfillmentJobEntity
            {
                MediaRequestId = request.Id,
                MediaId = mediaId,
                MediaType = MediaType.Movie,
                Title = title,
                Quality = Quality.FullHD,
                LibraryDestinationRootPath = "/movies",
                Status = FulfillmentStatus.Completed
            };
            Db.FulfillmentJobs.Add(job);
            await Db.SaveChangesAsync();
            return (request, job);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
