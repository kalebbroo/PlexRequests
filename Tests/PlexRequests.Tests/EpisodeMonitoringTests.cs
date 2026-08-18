using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Services.Jobs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public class EpisodeMonitoringTests
{
    private static readonly MonitoringPreferencesEntity Preferences = new()
    {
        MaxSearchAttemptsPerEpisode = 3
    };

    [Fact]
    public void AiredMissingEpisode_IsRearmedAfterFormerAttemptLimit()
    {
        var before = DateTime.UtcNow;
        var row = new AirScheduleEntity
        {
            Monitored = true,
            HasFile = false,
            AirsAtUtc = before.AddDays(-1),
            SearchState = AirSearchState.Skipped,
            SearchAttempts = 24,
            NextSearchAt = null
        };

        CalendarRefreshJob.ApplyState(row, Preferences);

        Assert.Equal(AirSearchState.Due, row.SearchState);
        Assert.NotNull(row.NextSearchAt);
        Assert.InRange(row.NextSearchAt!.Value, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void SearchingEpisode_AlwaysKeepsAReconciliationDeadline()
    {
        var before = DateTime.UtcNow;
        var row = new AirScheduleEntity
        {
            Monitored = true,
            HasFile = false,
            AirsAtUtc = before.AddHours(-1),
            SearchState = AirSearchState.Searching,
            NextSearchAt = null
        };

        CalendarRefreshJob.ApplyState(row, Preferences);

        Assert.Equal(AirSearchState.Searching, row.SearchState);
        Assert.NotNull(row.NextSearchAt);
        Assert.InRange(row.NextSearchAt!.Value, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void AcquiredEpisode_HasNoFurtherWakeTime()
    {
        var row = new AirScheduleEntity
        {
            Monitored = true,
            HasFile = true,
            AirsAtUtc = DateTime.UtcNow.AddDays(-1),
            SearchState = AirSearchState.Searching,
            NextSearchAt = DateTime.UtcNow
        };

        CalendarRefreshJob.ApplyState(row, Preferences);

        Assert.Equal(AirSearchState.Acquired, row.SearchState);
        Assert.Null(row.NextSearchAt);
    }

    [Fact]
    public void FutureEpisode_RemainsScheduledForItsAirWindow()
    {
        var airWindow = DateTime.UtcNow.AddDays(2);
        var row = new AirScheduleEntity
        {
            Monitored = true,
            HasFile = false,
            AirsAtUtc = airWindow,
            SearchState = AirSearchState.Due,
            NextSearchAt = DateTime.UtcNow
        };

        CalendarRefreshJob.ApplyState(row, Preferences);

        Assert.Equal(AirSearchState.NotDue, row.SearchState);
        Assert.Equal(airWindow, row.NextSearchAt);
    }

    [Fact]
    public async Task DurableMonitorChildIdentity_IsUniquePerAnchorAndSeason()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.MediaRequests.Add(new MediaRequestEntity
        {
            MediaId = 60625,
            MediaType = MediaType.TvShow,
            Title = "Rick and Morty monitor S9",
            MonitoringAnchorId = 10,
            MonitoringSeasonNumber = 9
        });
        await db.SaveChangesAsync();

        db.MediaRequests.Add(new MediaRequestEntity
        {
            MediaId = 60625,
            MediaType = MediaType.TvShow,
            Title = "duplicate monitor S9",
            MonitoringAnchorId = 10,
            MonitoringSeasonNumber = 9
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Request_CannotOwnTwoActiveFulfillmentJobs_ButRetainsTerminalHistory()
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
            Title = "Rick and Morty"
        };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();

        db.FulfillmentJobs.Add(new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            Title = request.Title,
            Status = FulfillmentStatus.PartiallyCompleted
        });
        db.FulfillmentJobs.Add(new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            Title = request.Title,
            Status = FulfillmentStatus.Queued
        });
        await db.SaveChangesAsync();

        db.FulfillmentJobs.Add(new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            Title = request.Title,
            Status = FulfillmentStatus.Deferred
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void NewEpisode_WidensAndWakesADeferredSeasonSearch()
    {
        var now = new DateTime(2026, 8, 18, 3, 0, 0, DateTimeKind.Utc);
        var child = new MediaRequestEntity { RequestedEpisodesCsv = "S9E4" };
        var job = new FulfillmentJobEntity
        {
            RequestedEpisodesCsv = "S9E4",
            SeasonTargetsJson = "stale",
            Status = FulfillmentStatus.Deferred,
            NextRetryAt = now.AddHours(20)
        };

        MediaRequestService.RefreshPendingMonitorScope(child, job, "S9E4,S9E5", now);

        Assert.Equal("S9E4,S9E5", child.RequestedEpisodesCsv);
        Assert.Equal("S9E4,S9E5", job.RequestedEpisodesCsv);
        Assert.Null(job.SeasonTargetsJson);
        Assert.Equal(FulfillmentStatus.Queued, job.Status);
        Assert.Null(job.NextRetryAt);
        Assert.Equal(now, job.LastUpdatedAt);
    }
}
