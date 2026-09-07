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

public sealed class FulfillmentTransferServiceTests
{
    [Fact]
    public async Task RetryingSameBackendIdReactivatesTerminalTrackingWithCurrentIntent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var request = new MediaRequestEntity { MediaId = 7, MediaType = MediaType.TvShow, Title = "Anime" };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();
        var job = new FulfillmentJobEntity
        {
            MediaRequestId = request.Id, MediaId = request.MediaId,
            MediaType = request.MediaType, Title = request.Title
        };
        db.FulfillmentJobs.Add(job);
        await db.SaveChangesAsync();
        const string transferId = "0936f1e8a2c3968ad47f3331919a065fcd81e985";
        db.FulfillmentTransfers.Add(new FulfillmentTransferEntity
        {
            FulfillmentJobId = job.Id,
            TransferId = transferId,
            State = TransferTrackingState.Failed,
            Progress = 22.5,
            Seeds = 10,
            Peers = 3,
            TotalSizeBytes = 20_000,
            FailReason = "old attempt was stopped",
            Season = 1,
            NeededEpisodesCsv = "1,2"
        });
        await db.SaveChangesAsync();
        var service = new FulfillmentTransferService(db, NullLogger<FulfillmentTransferService>.Instance);

        var registered = await service.RegisterAsync(job.Id,
        [
            new TrackedTransferDto
            {
                FulfillmentJobId = job.Id,
                TransferId = transferId,
                ReleaseName = "safe retry",
                Season = 4,
                SourceSeason = 6,
                FractionalEpisodeInsertionAfter = 3,
                IsPack = true,
                NeededEpisodes = [1, 2, 3],
                NeededEpisodeRefs =
                [
                    new EpisodeRef { Season = 4, Episode = 1 },
                    new EpisodeRef { Season = 4, Episode = 2 }
                ],
                Resolution = 1080
            }
        ]);

        Assert.Equal(1, registered);
        var row = await db.FulfillmentTransfers.SingleAsync();
        Assert.Equal(TransferTrackingState.Active, row.State);
        Assert.Equal(0, row.Progress);
        Assert.Equal(0, row.Seeds);
        Assert.Equal(0, row.Peers);
        Assert.Equal(0, row.TotalSizeBytes);
        Assert.Null(row.FailReason);
        Assert.Null(row.LastSeenAt);
        Assert.Equal("safe retry", row.ReleaseName);
        Assert.Equal(4, row.Season);
        Assert.Equal(6, row.SourceSeason);
        Assert.Equal(3, row.FractionalEpisodeInsertionAfter);
        Assert.Equal("1,2,3", row.NeededEpisodesCsv);
        Assert.Contains("\"Season\":4", row.NeededEpisodeRefsJson);
        Assert.Equal(1080, row.Resolution);
        var active = Assert.Single(await service.GetActiveAsync());
        Assert.Equal(6, active.SourceSeason);
        Assert.Equal(3, active.FractionalEpisodeInsertionAfter);
    }

    [Fact]
    public async Task Missing_backend_transfer_with_an_import_audit_is_recorded_as_imported()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var request = new MediaRequestEntity { MediaId = 7, MediaType = MediaType.TvShow, Title = "Show" };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();
        var job = new FulfillmentJobEntity
        {
            MediaRequestId = request.Id, MediaId = request.MediaId,
            MediaType = request.MediaType, Title = request.Title
        };
        db.FulfillmentJobs.Add(job);
        await db.SaveChangesAsync();
        const string transferId = "imported-transfer";
        db.FulfillmentTransfers.Add(new FulfillmentTransferEntity
        {
            FulfillmentJobId = job.Id, TransferId = transferId,
            Protocol = AcquisitionProtocol.Torrent, State = TransferTrackingState.Active
        });
        db.ImportedFiles.Add(new ImportedFileEntity
        {
            FulfillmentJobId = job.Id, TransferId = transferId,
            Protocol = AcquisitionProtocol.Torrent, SourcePath = "/downloads/show.mkv",
            DestinationPath = "/library/show.mkv", FileType = "video"
        });
        await db.SaveChangesAsync();
        var service = new FulfillmentTransferService(db, NullLogger<FulfillmentTransferService>.Instance);

        await service.ApplyAsync([
            new TransferStateUpdateDto
            {
                TransferId = transferId, Protocol = AcquisitionProtocol.Torrent,
                State = TransferTrackingState.Missing, Reason = "gone after cleanup"
            }
        ]);

        var transfer = await db.FulfillmentTransfers.SingleAsync();
        Assert.Equal(TransferTrackingState.Imported, transfer.State);
        Assert.Null(transfer.FailReason);
        Assert.NotNull(transfer.ImportedAt);
    }

    [Fact]
    public async Task Canonical_cross_season_targets_round_trip_through_durable_tracking()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var request = new MediaRequestEntity { MediaId = 7, MediaType = MediaType.TvShow, Title = "Anime" };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();
        var job = new FulfillmentJobEntity
        {
            MediaRequestId = request.Id, MediaId = request.MediaId,
            MediaType = request.MediaType, Title = request.Title
        };
        db.FulfillmentJobs.Add(job);
        await db.SaveChangesAsync();
        var service = new FulfillmentTransferService(db, NullLogger<FulfillmentTransferService>.Instance);

        await service.RegisterAsync(job.Id,
        [
            new TrackedTransferDto
            {
                FulfillmentJobId = job.Id,
                TransferId = "cross-season",
                IsPack = true,
                NeededEpisodeRefs =
                [
                    new EpisodeRef { Season = 2, Episode = 1 },
                    new EpisodeRef { Season = 1, Episode = 12 },
                    new EpisodeRef { Season = 2, Episode = 1 }
                ]
            }
        ]);
        db.ChangeTracker.Clear();

        var transfer = Assert.Single(await service.GetForJobAsync(job.Id));
        Assert.Equal([(1, 12), (2, 1)], transfer.NeededEpisodeRefs
            .Select(x => (x.Season, x.Episode)).ToList());
        Assert.NotNull((await db.FulfillmentTransfers.SingleAsync()).NeededEpisodeRefsJson);
    }

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
        db.FulfillmentTransfers.AddRange(
            new FulfillmentTransferEntity { FulfillmentJobId = firstJob.Id, TransferId = torrentId },
            new FulfillmentTransferEntity { FulfillmentJobId = secondJob.Id, TransferId = torrentId },
            // Backend ids are opaque and may collide across protocols. This row must remain independent.
            new FulfillmentTransferEntity
            {
                FulfillmentJobId = secondJob.Id,
                TransferId = torrentId,
                Protocol = AcquisitionProtocol.DirectAudio
            });
        await db.SaveChangesAsync();

        var service = new FulfillmentTransferService(
            db,
            NullLogger<FulfillmentTransferService>.Instance);

        var active = await service.GetActiveAsync();
        Assert.Equal(2, active.Count);
        var current = Assert.Single(active, x => x.Protocol == AcquisitionProtocol.Torrent);
        Assert.Equal(secondJob.Id, current.FulfillmentJobId);

        var changed = await service.ApplyAsync(new[]
        {
            new TransferStateUpdateDto
            {
                TransferId = torrentId,
                State = TransferTrackingState.Active,
                Progress = 42.5,
                Seeds = 8,
                Peers = 3,
                TotalSizeBytes = 1_000
            }
        });

        var rows = await db.FulfillmentTransfers.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, changed);
        Assert.Equal(3, rows.Count);
        Assert.All(rows.Where(row => row.Protocol == AcquisitionProtocol.Torrent), row =>
        {
            Assert.Equal(42.5, row.Progress);
            Assert.Equal(8, row.Seeds);
            Assert.Equal(3, row.Peers);
            Assert.Equal(1_000, row.TotalSizeBytes);
            Assert.NotNull(row.LastSeenAt);
        });
        var direct = Assert.Single(rows, row => row.Protocol == AcquisitionProtocol.DirectAudio);
        Assert.Equal(0, direct.Progress);
        Assert.Null(direct.LastSeenAt);
    }
}
