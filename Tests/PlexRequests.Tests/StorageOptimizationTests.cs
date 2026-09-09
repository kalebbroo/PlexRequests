using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

    private static ImportedFileEntity Video(int id, int jobId, string path, int? season,
        string codec, long bytes) => new()
        {
            Id = id,
            FulfillmentJobId = jobId,
            DestinationPath = path,
            SourcePath = "/downloads/file.mkv",
            FileType = "video",
            SeasonNumber = season,
            EpisodeNumber = season.HasValue ? 1 : null,
            ResolutionHeight = 1080,
            SizeBytes = bytes,
            MediaTracksJson = $$"""{"hasVideo":true,"video":[{"type":"video","codec":"{{codec}}","width":1920,"height":1080}]}""",
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
