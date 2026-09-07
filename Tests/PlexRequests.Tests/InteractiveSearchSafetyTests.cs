using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class InteractiveSearchSafetyTests
{
    [Fact]
    public async Task ForcedGrabPreservesOutstandingSeasonTargetsFromSupersededJob()
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
            RequestedSeasonsCsv = "1,2",
            Title = "Monogatari",
            IsAnime = true,
            Status = RequestStatus.Searching
        };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();

        var targets = new List<SeasonTarget>
        {
            new() { Season = 1, EpisodeCount = 2, MissingEpisodes = [1, 2] },
            new() { Season = 2, EpisodeCount = 1, MissingEpisodes = [1] }
        };
        db.FulfillmentJobs.Add(new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            MediaKind = MediaKind.Series,
            RequestScopeKind = request.RequestScopeKind,
            RequestedSeasonsCsv = request.RequestedSeasonsCsv,
            SeasonTargetsJson = JsonSerializer.Serialize(targets),
            Title = request.Title,
            IsAnime = true,
            Status = FulfillmentStatus.Deferred,
            Quality = Quality.Any
        });
        var task = new SearchTaskEntity
        {
            MediaRequestId = request.Id,
            MediaType = request.MediaType,
            MediaId = request.MediaId,
            MediaKind = MediaKind.Series,
            RequestScopeKind = RequestScopeKind.Series,
            Title = request.Title,
            Status = SearchTaskStatus.Completed,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };
        db.SearchTasks.Add(task);
        await db.SaveChangesAsync();

        var service = new InteractiveSearchService(db, null!,
            NullLogger<InteractiveSearchService>.Instance);
        var result = await service.GrabAsync(task.Id,
            "magnet:?xt=urn:btih:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            "[MTBB] Monogatari Series (BD 1080p)", 4, null);

        Assert.True(result.ok, result.error);
        var manual = await db.FulfillmentJobs.SingleAsync(job => job.IsManualGrab);
        Assert.Equal("1,2", manual.RequestedSeasonsCsv);
        Assert.Equal(JsonSerializer.Serialize(targets), manual.SeasonTargetsJson);
    }
}
