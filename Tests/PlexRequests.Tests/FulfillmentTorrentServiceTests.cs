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

public sealed class FulfillmentTorrentServiceTests
{
    [Fact]
    public async Task One_physical_torrent_updates_every_job_mapping()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var request = new MediaRequestEntity
        {
            MediaId = 1,
            MediaType = MediaType.TvShow,
            Title = "Shared release"
        };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();

        var firstJob = new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            Title = request.Title,
            // Historical/terminal job mappings remain reconcilable while the request owns one current job.
            Status = FulfillmentStatus.Completed
        };
        var secondJob = new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            Title = request.Title
        };
        db.FulfillmentJobs.AddRange(firstJob, secondJob);
        await db.SaveChangesAsync();

        const string torrentId = "5282169f1f68b449306b424802296d1e7d730f4d";
        db.FulfillmentTorrents.AddRange(
            new FulfillmentTorrentEntity { FulfillmentJobId = firstJob.Id, TorrentId = torrentId },
            new FulfillmentTorrentEntity { FulfillmentJobId = secondJob.Id, TorrentId = torrentId });
        await db.SaveChangesAsync();

        var service = new FulfillmentTorrentService(
            db,
            NullLogger<FulfillmentTorrentService>.Instance);

        var active = await service.GetActiveAsync();
        var current = Assert.Single(active);
        Assert.Equal(secondJob.Id, current.FulfillmentJobId);

        var changed = await service.ApplyAsync(new[]
        {
            new TorrentStateUpdateDto
            {
                TorrentId = torrentId,
                State = TorrentTrackingState.Active,
                Progress = 42.5,
                Seeds = 8,
                Peers = 3,
                TotalSizeBytes = 1_000
            }
        });

        var rows = await db.FulfillmentTorrents.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, changed);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(42.5, row.Progress);
            Assert.Equal(8, row.Seeds);
            Assert.Equal(3, row.Peers);
            Assert.Equal(1_000, row.TotalSizeBytes);
            Assert.NotNull(row.LastSeenAt);
        });
    }
}
