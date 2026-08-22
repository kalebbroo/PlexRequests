using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Services.Background;
using PlexRequestsHosted.Services.Jobs;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class MediaIssueReplacementTests
{
    [Fact]
    public async Task Admin_report_queues_safe_episode_replacement_and_blocklists_bad_source()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var request = new MediaRequestEntity
        {
            MediaId = 60625,
            MediaType = MediaType.TvShow,
            RequestScopeKind = RequestScopeKind.Series,
            Title = "Rick and Morty",
            Status = RequestStatus.Available,
            RequestedBy = "kaleb"
        };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();
        var oldJob = new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            RequestScopeKind = RequestScopeKind.Series,
            Title = request.Title,
            Status = FulfillmentStatus.Completed
        };
        db.FulfillmentJobs.Add(oldJob);
        await db.SaveChangesAsync();
        const string badHash = "0123456789abcdef0123456789abcdef01234567";
        db.ImportedFiles.Add(new ImportedFileEntity
        {
            FulfillmentJobId = oldJob.Id,
            TransferId = badHash,
            InfoHash = badHash,
            Protocol = AcquisitionProtocol.Torrent,
            DestinationPath = "/library/Rick and Morty/Season 09/Rick and Morty - S09E09.mkv",
            SourcePath = "/downloads/bad.mkv",
            FileType = "video",
            SeasonNumber = 9,
            EpisodeNumber = 9,
            ResolutionHeight = 1080,
            ReleaseName = "Rick.and.Morty.S09E09.1080p.BAD"
        });
        await db.SaveChangesAsync();

        var queue = new CapturingQueue { ReplacementJobId = 42 };
        var service = new MediaIssueService(
            db,
            new TestAuthProvider(userId: 1, admin: true),
            queue,
            new FixedQualityProfiles(),
            new ReleaseBlocklistService(db, NullLogger<ReleaseBlocklistService>.Instance));

        var result = await service.ReportIssueAsync(new MediaIssueReportDto
        {
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            Title = request.Title,
            Reason = "Wrong episode",
            Detail = "Episode 8 plays instead.",
            SeasonNumber = 9,
            EpisodeNumber = 9,
            RequestReplacement = true
        });

        Assert.True(result.Success);
        Assert.True(result.ReplacementQueued);
        var issue = await db.MediaIssues.SingleAsync();
        Assert.Equal(IssueStatus.ReplacementQueued, issue.Status);
        Assert.Equal(42, issue.ReplacementJobId);
        Assert.Equal([(9, 9)], queue.Episodes);
        Assert.Equal(Quality.FullHD, queue.Floor);
        Assert.Contains("S09E09.mkv", Assert.Single(queue.ReplacePaths));
        var blocked = await db.ReleaseBlocklist.SingleAsync();
        Assert.Equal(badHash, blocked.InfoHash);
        Assert.Equal(BlocklistReason.WrongContent, blocked.Reason);
    }

    [Fact]
    public async Task Revoked_admin_claim_cannot_auto_approve_a_replacement()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var user = new UserEntity { Username = "former-admin" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        db.UserProfiles.Add(new UserProfileEntity
        {
            UserId = user.Id, PlexUsername = user.Username, Roles = "User",
            Permissions = (int)UserPermission.AllRequests
        });
        await db.SaveChangesAsync();
        var auth = new TestAuthProvider(user.Id, admin: true); // deliberately stale cookie/circuit claim
        var access = new UserAccessService(db, auth, new ConfigurationBuilder().Build());
        var queue = new CapturingQueue { ReplacementJobId = 42 };
        var service = new MediaIssueService(db, auth, queue,
            new FixedQualityProfiles(), new ReleaseBlocklistService(db, NullLogger<ReleaseBlocklistService>.Instance), access);

        var result = await service.ReportIssueAsync(new MediaIssueReportDto
        {
            MediaId = 60625,
            MediaType = MediaType.TvShow,
            Title = "Rick and Morty",
            Reason = "Wrong episode",
            SeasonNumber = 9,
            EpisodeNumber = 9,
            RequestReplacement = true
        });

        Assert.True(result.Success);
        Assert.False(result.ReplacementQueued);
        var issue = await db.MediaIssues.SingleAsync();
        Assert.Equal(IssueStatus.Open, issue.Status);
        Assert.True(issue.ReplacementRequested);
        Assert.Null(queue.MediaIssueId);
    }

    [Fact]
    public async Task Stale_replacement_is_deferred_for_another_search_instead_of_cancelled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var request = new MediaRequestEntity
        {
            MediaId = 60625,
            MediaType = MediaType.TvShow,
            Title = "Rick and Morty",
            Status = RequestStatus.Available
        };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();
        var job = new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            Title = request.Title,
            Status = FulfillmentStatus.Claimed,
            Attempts = 3,
            IsUpgrade = true,
            IsReplacement = true,
            LastUpdatedAt = DateTime.UtcNow.AddHours(-1)
        };
        db.FulfillmentJobs.Add(job);
        await db.SaveChangesAsync();
        var reaper = new FulfillmentReaperService(db, null!, new ConfigurationBuilder().Build(),
            NullLogger<FulfillmentReaperService>.Instance);

        var count = await reaper.RunOnceAsync(staleMinutes: 1, maxAttempts: 3, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal(FulfillmentStatus.Deferred, job.Status);
        Assert.NotNull(job.NextRetryAt);
        Assert.Equal(RequestStatus.Available, request.Status);

        job.NextRetryAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        var search = new MissingSearchJob(db, NullLogger<MissingSearchJob>.Instance);
        var requeued = await search.ExecuteAsync(new JobContext(null, Manual: false), CancellationToken.None);

        Assert.Equal(JobRunStatus.Succeeded, requeued.Status);
        Assert.Equal(FulfillmentStatus.Queued, job.Status);
        Assert.Null(job.NextRetryAt);
    }

    private sealed class TestAuthProvider(int userId, bool admin) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, admin ? "admin" : "member"),
                new("user_id", userId.ToString()),
                new(ClaimTypes.Role, admin ? "Admin" : "User")
            };
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))));
        }
    }

    private sealed class CapturingQueue : IFulfillmentQueue
    {
        public int? ReplacementJobId { get; init; }
        public int? MediaIssueId { get; private set; }
        public Quality Floor { get; private set; }
        public List<string> ReplacePaths { get; private set; } = new();
        public List<(int season, int episode)> Episodes { get; private set; } = new();

        public Task<int?> EnqueueReplacementAsync(MediaRequestDto request, Quality floor,
            IReadOnlyList<string> replacePaths, IReadOnlyList<(int season, int episode)> episodes, int mediaIssueId)
        {
            Floor = floor;
            ReplacePaths = replacePaths.ToList();
            Episodes = episodes.ToList();
            MediaIssueId = mediaIssueId;
            return Task.FromResult(ReplacementJobId);
        }

        public Task<bool> EnqueueAsync(MediaRequestDto request, bool force = false) => Task.FromResult(false);
        public Task<List<FulfillmentJobDto>> ClaimNextAsync(string workerId, int max = 1) => Task.FromResult(new List<FulfillmentJobDto>());
        public Task<FulfillmentJobDto?> GetJobAsync(int jobId) => Task.FromResult<FulfillmentJobDto?>(null);
        public Task<bool> ReportProgressAsync(int jobId, int progress) => Task.FromResult(false);
        public Task MarkCompletedAsync(int mediaRequestId) => Task.CompletedTask;
        public Task MarkFailedAsync(int mediaRequestId, string reason) => Task.CompletedTask;
        public Task MarkPartiallyCompletedAsync(int mediaRequestId, string reason) => Task.CompletedTask;
        public Task<DeferResult> MarkDeferredAsync(int jobId, string reason, bool candidatesRejected = false) =>
            Task.FromResult(new DeferResult(false, false, 0, null, false));
        public Task MarkUpgradeExhaustedAsync(int jobId) => Task.CompletedTask;
        public Task<bool> EnqueueUpgradeAsync(MediaRequestDto request, Quality target,
            IReadOnlyList<string> replacePaths, IReadOnlyList<(int season, int episode)> episodes) => Task.FromResult(false);
        public Task<Quality> RecomputeAchievedQualityAsync(int mediaRequestId) => Task.FromResult(Quality.Any);
        public Task<Dictionary<(int Season, int Episode), int>> GetPlexEpisodeHeightsAsync(MediaRequestDto request) =>
            Task.FromResult(new Dictionary<(int Season, int Episode), int>());
    }

    private sealed class FixedQualityProfiles : IQualityProfileService
    {
        public Task<int> ResolveProfileIdAsync(MediaType mediaType, int tmdbId, IEnumerable<string>? genres,
            int? requesterChoiceId, int? userId, string? library = null) => Task.FromResult(3);
        public Task<Quality> GetCutoffQualityAsync(int profileId) => Task.FromResult(Quality.FullHD);
        public Task<List<QualityDefinitionDto>> GetDefinitionsAsync() => throw new NotSupportedException();
        public Task<List<QualityProfileDto>> GetProfilesAsync() => throw new NotSupportedException();
        public Task<List<QualityProfileSummaryDto>> GetSelectableProfilesAsync(int? userId, MediaType mediaType) => throw new NotSupportedException();
        public Task<QualityProfileDto?> GetProfileAsync(int id) => throw new NotSupportedException();
        public Task<QualityProfileDto> CreateProfileAsync(string name) => throw new NotSupportedException();
        public Task<bool> UpdateProfileAsync(QualityProfileDto profile) => throw new NotSupportedException();
        public Task<(bool ok, string? error)> DeleteProfileAsync(int id) => throw new NotSupportedException();
        public Task<bool> SetDefaultProfileAsync(int id) => throw new NotSupportedException();
        public Task<List<ProfileAssignmentRuleDto>> GetAssignmentRulesAsync() => throw new NotSupportedException();
        public Task<ProfileAssignmentRuleDto> CreateAssignmentRuleAsync() => throw new NotSupportedException();
        public Task<(bool ok, string? error)> UpdateAssignmentRuleAsync(ProfileAssignmentRuleDto rule) => throw new NotSupportedException();
        public Task<bool> DeleteAssignmentRuleAsync(int id) => throw new NotSupportedException();
        public Task<bool> MoveAssignmentRuleAsync(int id, bool up) => throw new NotSupportedException();
    }
}
