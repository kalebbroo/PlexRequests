using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Services.Jobs;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using System.Text.Json;
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

    [Fact]
    public void ActiveSeriesJobOwnsOnlyItsDurableRemainingTargetsAndImports()
    {
        var job = new FulfillmentJobEntity
        {
            Id = 255,
            Status = FulfillmentStatus.Downloading,
            RequestScopeKind = RequestScopeKind.Series,
            RequestedSeasonsCsv = "1,2,3,4,5",
            SeasonTargetsJson = JsonSerializer.Serialize(new[]
            {
                new SeasonTarget { Season = 3, EpisodeCount = 23, MissingEpisodes = [1, 2, 3] },
                new SeasonTarget { Season = 5, EpisodeCount = 15, MissingEpisodes = [5, 6, 10] }
            })
        };
        var imported = new HashSet<(int season, int episode)> { (5, 1), (5, 2) };

        Assert.True(MediaRequestService.ActiveJobsCoverMonitoredEpisodes(
            [job], imported, [(5, 1), (5, 5), (3, 2)], anchorRequestsAllSeasons: true));
        Assert.False(MediaRequestService.ActiveJobsCoverMonitoredEpisodes(
            [job], imported, [(5, 1), (5, 9)], anchorRequestsAllSeasons: true));
        Assert.Equal([(5, 9)], MediaRequestService.UncoveredMonitoredEpisodes(
            [job], imported, [(5, 1), (5, 5), (5, 9)], anchorRequestsAllSeasons: true));
        Assert.False(MediaRequestService.ActiveJobsCoverMonitoredEpisodes(
            [job], imported, [(6, 1)], anchorRequestsAllSeasons: true));
    }

    [Fact]
    public void ActiveSeasonSnapshotCoversEpisodesWhenMetadataTargetsAreUnavailable()
    {
        var job = new FulfillmentJobEntity
        {
            Status = FulfillmentStatus.Deferred,
            RequestedSeasonsCsv = "4,5",
            SeasonTargetsJson = "malformed legacy json"
        };

        Assert.True(MediaRequestService.ActiveJobsCoverMonitoredEpisodes(
            [job], new HashSet<(int, int)>(), [(5, 14)], anchorRequestsAllSeasons: false));
        Assert.False(MediaRequestService.ActiveJobsCoverMonitoredEpisodes(
            [job], new HashSet<(int, int)>(), [(3, 14)], anchorRequestsAllSeasons: false));
    }

    [Fact]
    public void TerminalOrReplacementJobsDoNotClaimBroadSeriesOwnership()
    {
        var cancelled = new FulfillmentJobEntity
        {
            Status = FulfillmentStatus.Cancelled,
            RequestScopeKind = RequestScopeKind.Series
        };
        var replacement = new FulfillmentJobEntity
        {
            Status = FulfillmentStatus.Downloading,
            RequestScopeKind = RequestScopeKind.Series,
            IsReplacement = true
        };

        Assert.False(MediaRequestService.ActiveJobsCoverMonitoredEpisodes(
            [cancelled, replacement], new HashSet<(int, int)>(), [(1, 1)],
            anchorRequestsAllSeasons: true));
    }
}
