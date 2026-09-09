using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

public sealed class StorageOptimizationTests
{
    [Fact]
    public async Task PreviewScopesSeriesBySeasonAndSelectsOnlyFilesThatMissCombinedGoals()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var request = new MediaRequestEntity
        {
            Id = 10,
            MediaId = 100,
            MediaType = MediaType.TvShow,
            RequestScopeKind = RequestScopeKind.Series,
            Title = "Efficient Show",
            Status = RequestStatus.Available
        };
        var job = new FulfillmentJobEntity
        {
            Id = 20,
            MediaRequestId = 10,
            MediaId = 100,
            MediaType = MediaType.TvShow,
            MediaKind = MediaKind.Series,
            RequestScopeKind = RequestScopeKind.Series,
            Title = request.Title,
            Status = FulfillmentStatus.Completed
        };
        db.AddRange(request, job);
        db.ImportedFiles.AddRange(
            Video(1, job.Id, "/tv/show/s01e01.mkv", 1, "AVC", 2_000_000_000),
            Video(2, job.Id, "/tv/show/s02e01.mkv", 2, "HEVC", 1_000_000_000));
        await db.SaveChangesAsync();
        var capture = new CapturingQueue();
        var service = new StorageOptimizationService(db, capture, new EmptyFormats(), new ReleaseParser());

        var preview = await service.PreviewAsync(new StorageOptimizationRequestDto
        {
            RequestId = request.Id,
            Seasons = [1],
            RequireHevc = true,
            TargetQuality = Quality.Any,
            MinimumSavingsPercent = 10
        });

        Assert.True(preview.CanQueue, preview.Message);
        Assert.Equal(1, preview.SelectedFileCount);
        Assert.Equal(2_000_000_000, preview.CurrentBytes);
        Assert.Equal([1], preview.Seasons);

        var queued = await service.QueueAsync(new StorageOptimizationRequestDto
        {
            RequestId = request.Id,
            Seasons = [1],
            RequireHevc = true,
            TargetQuality = Quality.Any,
            MinimumSavingsPercent = 10
        });
        Assert.True(queued.Success);
        Assert.Equal(77, queued.JobId);
        var target = Assert.Single(capture.Policy!.Targets);
        Assert.Equal("/tv/show/s01e01.mkv", target.DestinationPath);
        Assert.Equal((1, 1), (Assert.Single(target.EpisodeCoverage).Season,
            target.EpisodeCoverage[0].Episode));
    }

    [Fact]
    public async Task PreviewDoesNotQueueAWholeLibraryOrAlreadyCompliantScope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.MediaRequests.Add(new MediaRequestEntity
        { Id = 1, MediaId = 1, MediaType = MediaType.Movie, Title = "Done", Status = RequestStatus.Available });
        db.FulfillmentJobs.Add(new FulfillmentJobEntity
        {
            Id = 1,
            MediaRequestId = 1,
            MediaId = 1,
            MediaType = MediaType.Movie,
            MediaKind = MediaKind.Movie,
            Title = "Done",
            Status = FulfillmentStatus.Completed
        });
        db.ImportedFiles.Add(Video(1, 1, "/movies/done.mkv", null, "HEVC", 1_000_000_000));
        await db.SaveChangesAsync();
        var service = new StorageOptimizationService(db, new CapturingQueue(), new EmptyFormats(), new ReleaseParser());

        var noTitle = await service.PreviewAsync(new StorageOptimizationRequestDto { RequireHevc = true });
        var compliant = await service.PreviewAsync(new StorageOptimizationRequestDto
        { RequestId = 1, RequireHevc = true });

        Assert.False(noTitle.CanQueue);
        Assert.False(compliant.CanQueue);
        Assert.Contains("already satisfies", compliant.Message);
    }

    [Fact]
    public async Task HevcOptimizationRequiresObservedCodecButUnrelatedResolutionGoalDoesNot()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var request = new MediaRequestEntity
        {
            Id = 11, MediaId = 110, MediaType = MediaType.Movie, Title = "Inferred Movie",
            Status = RequestStatus.Available
        };
        var job = new FulfillmentJobEntity
        {
            Id = 12, MediaRequestId = request.Id, MediaId = request.MediaId,
            MediaType = request.MediaType, Title = request.Title, Status = FulfillmentStatus.Completed
        };
        var file = Video(13, job.Id, "/movies/inferred.mkv", null, "", 2_000_000_000);
        file.MediaTracksJson = null;
        file.ReleaseName = "Inferred.Movie.2025.1080p.x265-GROUP";
        db.AddRange(request, job, file);
        await db.SaveChangesAsync();
        var capture = new CapturingQueue();
        var service = new StorageOptimizationService(db, capture, new EmptyFormats(), new ReleaseParser());

        var blocked = await service.PreviewAsync(new StorageOptimizationRequestDto
        {
            RequestId = request.Id,
            RequireHevc = true,
            MinimumSavingsPercent = 0
        });
        var queueAttempt = await service.QueueAsync(new StorageOptimizationRequestDto
        {
            RequestId = request.Id,
            RequireHevc = true,
            MinimumSavingsPercent = 0
        });
        var qualityOnly = await service.PreviewAsync(new StorageOptimizationRequestDto
        {
            RequestId = request.Id,
            TargetQuality = Quality.UHD4K,
            RequireHevc = false,
            MinimumSavingsPercent = 0
        });

        Assert.False(blocked.CanQueue);
        Assert.True(blocked.RequiresCodecScan);
        Assert.Equal(1, blocked.UnknownCodecCount);
        Assert.Equal([file.Id], blocked.CodecScanFileIds);
        Assert.Contains("read-only codec scan", blocked.Message);
        Assert.False(queueAttempt.Success);
        Assert.Null(capture.Policy);
        Assert.True(qualityOnly.CanQueue, qualityOnly.Message);
        Assert.False(qualityOnly.RequiresCodecScan);
        var report = await service.GetEfficiencyReportAsync();
        Assert.Equal(1, report.UnknownCodecFileCount);
        Assert.Equal(0, report.ModernCodecFileCount);
        Assert.Equal("Needs scan", Assert.Single(report.Titles).CodecSummary);

        file.MediaTracksJson = """{"hasVideo":true,"video":[{"type":"video","codec":"AVC","width":1920,"height":1080}]}""";
        await db.SaveChangesAsync();
        var measured = await service.PreviewAsync(new StorageOptimizationRequestDto
        {
            RequestId = request.Id,
            RequireHevc = true,
            MinimumSavingsPercent = 0
        });
        Assert.True(measured.CanQueue, measured.Message);
        Assert.False(measured.RequiresCodecScan);
        Assert.Equal(0, measured.UnknownCodecCount);
    }

    [Fact]
    public async Task EfficiencyReportSeparatesMeasuredCodecStorageWithoutEstimatingSavings()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var request = new MediaRequestEntity
        {
            Id = 21, MediaId = 210, MediaType = MediaType.Movie, Title = "Measured Movie",
            Status = RequestStatus.Available
        };
        var job = new FulfillmentJobEntity
        {
            Id = 22, MediaRequestId = request.Id, MediaId = request.MediaId, MediaType = request.MediaType,
            Title = request.Title, Year = 2024, Status = FulfillmentStatus.Completed,
            LibraryDestinationName = "Movies"
        };
        db.AddRange(request, job);
        var unknown = Video(24, job.Id, "/movies/unknown.mkv", null, "", 3_000_000_000);
        unknown.MediaMetadataScanStatus = MediaMetadataScanStatus.Failed;
        unknown.MediaMetadataScanDetail = "file unavailable";
        db.ImportedFiles.AddRange(
            Video(20, job.Id, "/movies/h264.mkv", null, "AVC", 6_000_000_000, 2160, 3840),
            Video(21, job.Id, "/movies/h264.mkv", null, "AVC", 4_000_000_000, 2160, 3840),
            Video(22, job.Id, "/movies/hevc.mkv", null, "HEVC", 2_000_000_000),
            Video(23, job.Id, "/movies/av1.mkv", null, "AV1", 1_000_000_000),
            unknown);
        await db.SaveChangesAsync();
        var capture = new CapturingQueue();
        var service = new StorageOptimizationService(db, capture, new EmptyFormats(), new ReleaseParser());

        var report = await service.GetEfficiencyReportAsync();
        var title = Assert.Single(report.Titles);

        Assert.Equal(10_000_000_000, report.TotalBytes);
        Assert.Equal((2, 3_000_000_000), (report.ModernCodecFileCount, report.ModernCodecBytes));
        Assert.Equal((1, 4_000_000_000), (report.LegacyCodecFileCount, report.LegacyCodecBytes));
        Assert.Equal((1, 3_000_000_000), (report.UnknownCodecFileCount, report.UnknownCodecBytes));
        Assert.Equal(1, report.MetadataScanFailedCount);
        Assert.Equal("file unavailable", title.MetadataScanDetail);
        Assert.Equal(1, report.KnownCandidateTitleCount);
        Assert.Equal(7_000_000_000, title.ReviewBytes);
        Assert.Equal((1, 4_000_000_000), (title.UltraHdFileCount, title.UltraHdBytes));
        Assert.Equal(["Movies"], title.Libraries);
        Assert.Null(capture.Policy);
        Assert.Equal(FulfillmentStatus.Completed, (await db.FulfillmentJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task ActivityExplainsFrozenScopeAndPersistsActualSavings()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var request = new MediaRequestEntity
        {
            Id = 31,
            MediaId = 310,
            MediaType = MediaType.TvShow,
            Title = "Compact Show",
            Status = RequestStatus.Available,
            PosterUrl = "/poster.jpg"
        };
        var policy = new StorageOptimizationPolicyDto
        {
            RequiredVideoCodec = "hevc",
            TargetQuality = Quality.FullHD,
            MinimumSavingsPercent = 25,
            Targets =
            [
                new StorageOptimizationTargetDto
                {
                    DestinationPath = "/tv/show/s01e01.mkv", CurrentSizeBytes = 2_000_000_000,
                    EpisodeCoverage = [new EpisodeRef { Season = 1, Episode = 1 }]
                }
            ]
        };
        db.AddRange(request, new FulfillmentJobEntity
        {
            Id = 32,
            MediaRequestId = request.Id,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            Title = request.Title,
            Year = 2025,
            Status = FulfillmentStatus.Completed,
            Progress = 100,
            StorageOptimizationPolicyJson = JsonSerializer.Serialize(policy),
            StorageOptimizationReplacementBytes = 1_200_000_000,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            CompletedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var service = new StorageOptimizationService(db, new CapturingQueue(), new EmptyFormats(), new ReleaseParser());

        var activity = Assert.Single(await service.GetActivityAsync());

        Assert.Equal("Optimization completed", activity.Stage);
        Assert.Equal([1], activity.Seasons);
        Assert.Equal(["HEVC (H.265)", "1080p", "Save at least 25%"], activity.Goals);
        Assert.Equal(2_000_000_000, activity.OriginalBytes);
        Assert.Equal(1_200_000_000, activity.ReplacementBytes);
        Assert.Equal(800_000_000, activity.BytesSaved);
        Assert.False(activity.CanCancel);
        Assert.False(activity.CanRetry);
    }

    [Fact]
    public async Task CancelAndRetryOnlyChangeOptimizerJobAndKeepRequestAvailable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var request = new MediaRequestEntity
        {
            Id = 41,
            MediaId = 410,
            MediaType = MediaType.Movie,
            Title = "Safe Movie",
            Status = RequestStatus.Available,
            AvailableAt = DateTime.UtcNow.AddDays(-1)
        };
        var job = new FulfillmentJobEntity
        {
            Id = 42,
            MediaRequestId = request.Id,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            Title = request.Title,
            Status = FulfillmentStatus.Queued,
            StorageOptimizationPolicyJson = JsonSerializer.Serialize(new StorageOptimizationPolicyDto
            {
                RequiredVideoCodec = "hevc",
                Targets = [new StorageOptimizationTargetDto { DestinationPath = "/movies/safe.mkv", CurrentSizeBytes = 10 }]
            })
        };
        db.AddRange(request, job);
        await db.SaveChangesAsync();
        var service = new StorageOptimizationService(db, new CapturingQueue(), new EmptyFormats(), new ReleaseParser());

        var cancelled = await service.CancelAsync(job.Id);
        await db.Entry(job).ReloadAsync();
        await db.Entry(request).ReloadAsync();
        Assert.True(cancelled.Success);
        Assert.Equal(FulfillmentStatus.Cancelled, job.Status);
        Assert.Equal(RequestStatus.Available, request.Status);

        // Simulate a row touched by the pre-hardening generic failure endpoint. Retrying repairs only this
        // optimizer's already-available request and preserves its immutable policy.
        request.Status = RequestStatus.Failed;
        request.DenialReason = "optimizer failed";
        await db.SaveChangesAsync();
        var retried = await service.RetryAsync(job.Id);
        await db.Entry(job).ReloadAsync();
        await db.Entry(request).ReloadAsync();
        Assert.True(retried.Success);
        Assert.Equal(FulfillmentStatus.Queued, job.Status);
        Assert.Equal(RequestStatus.Available, request.Status);
        Assert.Null(request.DenialReason);
        Assert.NotNull(job.StorageOptimizationPolicyJson);

        job.Status = FulfillmentStatus.Downloading;
        await db.SaveChangesAsync();
        var unsafeCancel = await service.CancelAsync(job.Id);
        Assert.False(unsafeCancel.Success);
        await db.Entry(job).ReloadAsync();
        Assert.Equal(FulfillmentStatus.Downloading, job.Status);

        var isolatedFailure = await service.RecordFailureAsync(request.Id, "replacement verification failed");
        await db.Entry(job).ReloadAsync();
        await db.Entry(request).ReloadAsync();
        Assert.True(isolatedFailure);
        Assert.Equal(FulfillmentStatus.Failed, job.Status);
        Assert.Equal("replacement verification failed", job.LastError);
        Assert.Equal(RequestStatus.Available, request.Status);
        Assert.True(Assert.Single(await service.GetActivityAsync()).CanRetry);
    }

    private static ImportedFileEntity Video(int id, int jobId, string path, int? season,
        string codec, long bytes, int height = 1080, int width = 1920) => new()
        {
            Id = id,
            FulfillmentJobId = jobId,
            DestinationPath = path,
            SourcePath = "/downloads/file.mkv",
            FileType = "video",
            SeasonNumber = season,
            EpisodeNumber = season.HasValue ? 1 : null,
            ResolutionHeight = height,
            SizeBytes = bytes,
            MediaTracksJson = $$"""{"hasVideo":true,"video":[{"type":"video","codec":"{{codec}}","width":{{width}},"height":{{height}}}]}""",
            EpisodeCoverage = season.HasValue
            ? [new ImportedEpisodeCoverageEntity { SeasonNumber = season.Value, EpisodeNumber = 1 }]
            : []
        };

    private sealed class EmptyFormats : ICustomFormatService
    {
        public Task<List<CustomFormatDto>> GetAllAsync() => Task.FromResult(new List<CustomFormatDto>());
        public Task<List<CustomFormatDto>> GetForProfileAsync(int profileId) => GetAllAsync();
        public Task<(bool ok, string? error)> SaveAsync(CustomFormatDto dto) => Task.FromResult<(bool, string?)>((true, null));
        public Task<(bool ok, string? error)> DeleteAsync(int id) => Task.FromResult<(bool, string?)>((true, null));
        public Task<bool> SetScoreAsync(int profileId, int formatId, int score) => Task.FromResult(true);
        public Task<bool> SetPreferenceAsync(int profileId, int formatId, CustomFormatPreference preference, int? advancedScore = null) => Task.FromResult(true);
        public Task<Dictionary<int, int>> ScoresForProfileAsync(int profileId) => Task.FromResult(new Dictionary<int, int>());
        public Task SeedAsync() => Task.CompletedTask;
    }

    private sealed class CapturingQueue : IFulfillmentQueue
    {
        public StorageOptimizationPolicyDto? Policy { get; private set; }
        public Task<int?> EnqueueOptimizationAsync(MediaRequestDto request, StorageOptimizationPolicyDto policy)
        { Policy = policy; return Task.FromResult<int?>(77); }
        public Task<bool> EnqueueAsync(MediaRequestDto request, bool force = false) => throw new NotSupportedException();
        public Task<List<FulfillmentJobDto>> ClaimNextAsync(string workerId, int max = 1) => throw new NotSupportedException();
        public Task<FulfillmentJobDto?> GetJobAsync(int jobId) => throw new NotSupportedException();
        public Task<bool> ReportProgressAsync(int jobId, int progress) => throw new NotSupportedException();
        public Task MarkCompletedAsync(int mediaRequestId) => throw new NotSupportedException();
        public Task MarkFailedAsync(int mediaRequestId, string reason) => throw new NotSupportedException();
        public Task MarkPartiallyCompletedAsync(int mediaRequestId, string reason) => throw new NotSupportedException();
        public Task<DeferResult> MarkDeferredAsync(int jobId, string reason, bool candidatesRejected = false) => throw new NotSupportedException();
        public Task MarkUpgradeExhaustedAsync(int jobId) => throw new NotSupportedException();
        public Task<bool> EnqueueUpgradeAsync(MediaRequestDto request, Quality target, IReadOnlyList<string> replacePaths, IReadOnlyList<(int season, int episode)> episodes) => throw new NotSupportedException();
        public Task<int?> EnqueueReplacementAsync(MediaRequestDto request, Quality floor, IReadOnlyList<string> replacePaths, IReadOnlyList<(int season, int episode)> episodes, int mediaIssueId) => throw new NotSupportedException();
        public Task<Quality> RecomputeAchievedQualityAsync(int mediaRequestId) => throw new NotSupportedException();
        public Task<Dictionary<(int Season, int Episode), int>> GetPlexEpisodeHeightsAsync(MediaRequestDto request) => throw new NotSupportedException();
    }
}
