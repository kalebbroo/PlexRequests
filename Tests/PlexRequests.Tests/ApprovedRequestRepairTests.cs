using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Jobs;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class ApprovedRequestRepairTests
{
    [Fact]
    public async Task Only_approved_requests_with_zero_job_history_are_reenqueued()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var orphan = Request(101, "Orphaned approval", RequestStatus.Approved, minutesAgo: 30);
        orphan.MediaType = MediaType.TvShow;
        orphan.RequestScopeKind = RequestScopeKind.Seasons;
        orphan.RequestedSeasonsCsv = "3,4,5";
        var pending = Request(102, "Still awaiting approval", RequestStatus.Pending, minutesAgo: 20);
        var deferred = Request(103, "Already has durable work", RequestStatus.Approved, minutesAgo: 10);
        db.MediaRequests.AddRange(orphan, pending, deferred);
        await db.SaveChangesAsync();
        db.FulfillmentJobs.Add(new FulfillmentJobEntity
        {
            MediaRequestId = deferred.Id,
            MediaId = deferred.MediaId,
            MediaType = deferred.MediaType,
            Title = deferred.Title,
            Status = FulfillmentStatus.Deferred,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var queue = new CapturingQueue();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Fulfillment:Enabled"] = "true" })
            .Build();
        var job = new ApprovedRequestRepairJob(
            db,
            queue,
            configuration,
            NullLogger<ApprovedRequestRepairJob>.Instance);

        var result = await job.ExecuteAsync(new JobContext(null, false), CancellationToken.None);

        Assert.Equal(JobRunStatus.Succeeded, result.Status);
        Assert.Equal(1, result.ItemsProcessed);
        var repaired = Assert.Single(queue.Requests);
        Assert.Equal(orphan.Id, repaired.Id);
        Assert.Equal([3, 4, 5], repaired.RequestedSeasons);
    }

    [Fact]
    public async Task Disabled_fulfillment_does_not_touch_orphaned_approvals()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.MediaRequests.Add(Request(101, "Orphaned approval", RequestStatus.Approved, minutesAgo: 1));
        await db.SaveChangesAsync();

        var queue = new CapturingQueue();
        var job = new ApprovedRequestRepairJob(
            db,
            queue,
            new ConfigurationBuilder().Build(),
            NullLogger<ApprovedRequestRepairJob>.Instance);

        var result = await job.ExecuteAsync(new JobContext(null, false), CancellationToken.None);

        Assert.Equal(JobRunStatus.Skipped, result.Status);
        Assert.Empty(queue.Requests);
    }

    private static MediaRequestEntity Request(int mediaId, string title, RequestStatus status, int minutesAgo) => new()
    {
        MediaId = mediaId,
        MediaType = MediaType.Movie,
        RequestScopeKind = RequestScopeKind.Title,
        Title = title,
        RequestedBy = "member",
        Status = status,
        RequestedAt = DateTime.UtcNow.AddMinutes(-minutesAgo),
        ApprovedAt = status == RequestStatus.Approved ? DateTime.UtcNow.AddMinutes(-minutesAgo + 1) : null
    };

    private sealed class CapturingQueue : IFulfillmentQueue
    {
        public List<MediaRequestDto> Requests { get; } = new();

        public Task<bool> EnqueueAsync(MediaRequestDto request, bool force = false)
        {
            Requests.Add(request);
            return Task.FromResult(true);
        }

        public Task<List<FulfillmentJobDto>> ClaimNextAsync(string workerId, int max = 1) =>
            Task.FromResult(new List<FulfillmentJobDto>());
        public Task<FulfillmentJobDto?> GetJobAsync(int jobId) => Task.FromResult<FulfillmentJobDto?>(null);
        public Task<bool> ReportProgressAsync(int jobId, int progress) => Task.FromResult(false);
        public Task MarkCompletedAsync(int mediaRequestId) => Task.CompletedTask;
        public Task MarkFailedAsync(int mediaRequestId, string reason) => Task.CompletedTask;
        public Task MarkPartiallyCompletedAsync(int mediaRequestId, string reason) => Task.CompletedTask;
        public Task<DeferResult> MarkDeferredAsync(int jobId, string reason, bool candidatesRejected = false) =>
            Task.FromResult(new DeferResult(false, false, 0, null, false));
        public Task MarkUpgradeExhaustedAsync(int jobId) => Task.CompletedTask;
        public Task<bool> EnqueueUpgradeAsync(MediaRequestDto request, Quality target,
            IReadOnlyList<string> replacePaths, IReadOnlyList<(int season, int episode)> episodes) =>
            Task.FromResult(false);
        public Task<int?> EnqueueReplacementAsync(MediaRequestDto request, Quality floor,
            IReadOnlyList<string> replacePaths, IReadOnlyList<(int season, int episode)> episodes, int mediaIssueId) =>
            Task.FromResult<int?>(null);
        public Task<Quality> RecomputeAchievedQualityAsync(int mediaRequestId) => Task.FromResult(Quality.Any);
        public Task<Dictionary<(int Season, int Episode), int>> GetPlexEpisodeHeightsAsync(MediaRequestDto request) =>
            Task.FromResult(new Dictionary<(int Season, int Episode), int>());
    }
}
