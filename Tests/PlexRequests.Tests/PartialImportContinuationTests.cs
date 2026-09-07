using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Background;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class PartialImportContinuationTests
{
    [Fact]
    public async Task PartialSeasonPackQueuesOnlyAuditProvenRemainingEpisodes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var request = new MediaRequestEntity
        {
            MediaId = 46195,
            MediaType = MediaType.TvShow,
            RequestScopeKind = RequestScopeKind.Series,
            Title = "Monogatari",
            Status = RequestStatus.Processing,
            IsAnime = true
        };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();
        var job = new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            Title = request.Title,
            IsAnime = true,
            Status = FulfillmentStatus.Downloading,
            RequestedSeasonsCsv = "1,2,4",
            SeasonTargetsJson = JsonSerializer.Serialize(new List<SeasonTarget>
            {
                new() { Season = 1, EpisodeCount = 2, MissingEpisodes = [1, 2] },
                new() { Season = 2, EpisodeCount = 1, MissingEpisodes = [1] },
                new() { Season = 4, EpisodeCount = 2, MissingEpisodes = [1, 2] }
            }),
            IsManualGrab = true,
            ForcedMagnet = "magnet:?xt=urn:btih:old",
            ForcedReleaseName = "old season pack",
            ForcedIndexerId = 7
        };
        db.FulfillmentJobs.Add(job);
        await db.SaveChangesAsync();
        db.ImportedFiles.AddRange(
            Video(job.Id, 4, 1),
            new ImportedFileEntity
            {
                FulfillmentJobId = job.Id,
                SourcePath = "/downloads/combined.mkv",
                DestinationPath = "/library/combined.mkv",
                FileType = "video",
                EpisodeCoverage =
                [
                    new() { SeasonNumber = 1, EpisodeNumber = 1 },
                    new() { SeasonNumber = 4, EpisodeNumber = 2 }
                ]
            },
            new ImportedFileEntity
            {
                FulfillmentJobId = job.Id,
                SourcePath = "/downloads/subtitle.ass",
                DestinationPath = "/library/subtitle.ass",
                FileType = "subtitle",
                SeasonNumber = 2,
                EpisodeNumber = 1
            });
        await db.SaveChangesAsync();

        var queue = Queue(db);
        var before = DateTime.UtcNow;
        await queue.MarkPartiallyCompletedAsync(request.Id, "one safe season imported");

        var persisted = await db.FulfillmentJobs.SingleAsync();
        var remaining = JsonSerializer.Deserialize<List<SeasonTarget>>(persisted.SeasonTargetsJson!);
        Assert.Equal(FulfillmentStatus.Queued, persisted.Status);
        Assert.Equal("1,2", persisted.RequestedSeasonsCsv);
        Assert.Collection(remaining!,
            season => { Assert.Equal(1, season.Season); Assert.Equal([2], season.MissingEpisodes); },
            season => { Assert.Equal(2, season.Season); Assert.Equal([1], season.MissingEpisodes); });
        Assert.NotNull(persisted.NextRetryAt);
        Assert.InRange(persisted.NextRetryAt!.Value, before.AddSeconds(55), before.AddMinutes(2));
        Assert.False(persisted.IsManualGrab);
        Assert.Null(persisted.ForcedMagnet);
        Assert.Null(persisted.ForcedReleaseName);
        Assert.Null(persisted.ForcedIndexerId);
        Assert.Contains("2 missing episode(s)", persisted.LastError);

        var persistedRequest = await db.MediaRequests.SingleAsync();
        Assert.Equal(RequestStatus.PartiallyAvailable, persistedRequest.Status);
        Assert.NotNull(persistedRequest.AvailableAt);
    }

    [Fact]
    public async Task CumulativeImportsCompleteExplicitEpisodeTargetInsteadOfSearchingAgain()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var request = new MediaRequestEntity
        {
            MediaId = 10, MediaType = MediaType.Anime, RequestScopeKind = RequestScopeKind.Episodes,
            Title = "Complete Anime", Status = RequestStatus.PartiallyAvailable
        };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();
        var job = new FulfillmentJobEntity
        {
            MediaRequestId = request.Id, MediaId = request.MediaId, MediaType = request.MediaType,
            Title = request.Title, Status = FulfillmentStatus.Downloading,
            RequestedEpisodesCsv = "S1E1,S1E2"
        };
        db.FulfillmentJobs.Add(job);
        await db.SaveChangesAsync();
        db.ImportedFiles.AddRange(Video(job.Id, 1, 1), Video(job.Id, 1, 2));
        await db.SaveChangesAsync();

        await Queue(db).MarkPartiallyCompletedAsync(request.Id, "last target imported");

        Assert.Equal(FulfillmentStatus.Completed, job.Status);
        Assert.Equal(100, job.Progress);
        Assert.Equal(RequestStatus.Available, request.Status);
        Assert.Null(job.NextRetryAt);
    }

    [Theory]
    [InlineData(MediaType.TvShow, true)]
    [InlineData(MediaType.Anime, true)]
    [InlineData(MediaType.Movie, false)]
    [InlineData(MediaType.Music, false)]
    public void AnimeUsesEpisodeAwareAvailability(MediaType mediaType, bool expected) =>
        Assert.Equal(expected, AvailabilityReconciliationService.UsesSeriesAvailability(mediaType));

    private static ImportedFileEntity Video(int jobId, int season, int episode) => new()
    {
        FulfillmentJobId = jobId,
        SourcePath = $"/downloads/S{season:00}E{episode:00}.mkv",
        DestinationPath = $"/library/S{season:00}E{episode:00}.mkv",
        FileType = "video",
        SeasonNumber = season,
        EpisodeNumber = episode
    };

    private static FulfillmentQueue Queue(AppDbContext db) => new(
        db, null!, null!, null!, null!, null!, null!, NullLogger<FulfillmentQueue>.Instance);
}
