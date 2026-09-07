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

public sealed class ReleaseBlocklistVersionTests
{
    [Fact]
    public async Task EpisodeMappingBlockIsCurrentOnlyForTheDecisionVersionThatCreatedIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var hash = new string('a', 40);

        Assert.True(await fixture.Service.BlockAsync(fixture.Job.Id, new BlocklistRequestDto
        {
            InfoHash = hash,
            SourceId = hash,
            ReleaseName = "Anime.S01.Pack",
            Reason = BlocklistReason.EpisodeMappingAmbiguous,
            Detail = "old parser could not prove the manifest"
        }));

        var row = await fixture.Db.ReleaseBlocklist.SingleAsync();
        Assert.Equal(ReleaseBlocklistPolicy.CurrentEpisodeMappingVersion, row.DecisionVersion);
        Assert.Contains(hash, await fixture.Service.HashesForRequestAsync(fixture.Job.Id));

        row.DecisionVersion = ReleaseBlocklistPolicy.CurrentEpisodeMappingVersion - 1;
        await fixture.Db.SaveChangesAsync();

        Assert.Empty(await fixture.Service.HashesForRequestAsync(fixture.Job.Id));
        Assert.Empty(await fixture.Service.ListAsync(fixture.Request.Id));
        Assert.Equal(1, await fixture.Service.PruneExpiredAsync());
        Assert.Empty(await fixture.Db.ReleaseBlocklist.ToListAsync());
    }

    [Fact]
    public async Task PermanentContentBlockDoesNotExpireWhenMappingRulesChange()
    {
        await using var fixture = await Fixture.CreateAsync();
        var hash = new string('b', 40);

        Assert.True(await fixture.Service.BlockAsync(fixture.Job.Id, new BlocklistRequestDto
        {
            InfoHash = hash,
            SourceId = hash,
            ReleaseName = "Wrong.Anime.Release",
            Reason = BlocklistReason.WrongContent,
            Detail = "confirmed wrong content"
        }));

        var row = await fixture.Db.ReleaseBlocklist.SingleAsync();
        Assert.Null(row.DecisionVersion);
        Assert.Contains(hash, await fixture.Service.HashesForRequestAsync(fixture.Job.Id));
        Assert.Equal(0, await fixture.Service.PruneExpiredAsync());
        Assert.Single(await fixture.Db.ReleaseBlocklist.ToListAsync());
    }

    [Fact]
    public async Task AutomaticMappingFailureCannotDowngradeADurableContentBlock()
    {
        await using var fixture = await Fixture.CreateAsync();
        var hash = new string('d', 40);

        Assert.True(await fixture.Service.BlockAsync(fixture.Job.Id, new BlocklistRequestDto
        {
            InfoHash = hash,
            SourceId = hash,
            ReleaseName = "Confirmed.Wrong.Anime",
            Reason = BlocklistReason.WrongContent,
            Detail = "admin-confirmed wrong title"
        }));
        Assert.True(await fixture.Service.BlockAsync(fixture.Job.Id, new BlocklistRequestDto
        {
            InfoHash = hash,
            SourceId = hash,
            ReleaseName = "Confirmed.Wrong.Anime",
            Reason = BlocklistReason.EpisodeMappingAmbiguous,
            Detail = "automatic parser could not map this manifest"
        }));

        var row = await fixture.Db.ReleaseBlocklist.SingleAsync();
        Assert.Equal(BlocklistReason.WrongContent, row.Reason);
        Assert.Null(row.DecisionVersion);
        Assert.Equal("admin-confirmed wrong title", row.Detail);
    }

    [Fact]
    public async Task ReobservedMappingFailureRefreshesALegacyEntryToTheCurrentVersion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var hash = new string('c', 40);
        fixture.Db.ReleaseBlocklist.Add(new ReleaseBlocklistEntity
        {
            InfoHash = hash,
            SourceId = hash,
            ReleaseName = "Anime.Legacy.Pack",
            NormalizedReleaseName = ReleaseBlocklistEntity.Normalize("Anime.Legacy.Pack"),
            MediaRequestId = fixture.Request.Id,
            MediaId = fixture.Job.MediaId,
            MediaType = fixture.Job.MediaType,
            Reason = BlocklistReason.EpisodeMappingAmbiguous,
            DecisionVersion = null
        });
        await fixture.Db.SaveChangesAsync();

        Assert.True(await fixture.Service.BlockAsync(fixture.Job.Id, new BlocklistRequestDto
        {
            InfoHash = hash,
            SourceId = hash,
            ReleaseName = "Anime.Legacy.Pack",
            Reason = BlocklistReason.EpisodeMappingAmbiguous,
            Detail = "current rules still reject it"
        }));

        var row = await fixture.Db.ReleaseBlocklist.SingleAsync();
        Assert.Equal(ReleaseBlocklistPolicy.CurrentEpisodeMappingVersion, row.DecisionVersion);
        Assert.Contains(hash, await fixture.Service.HashesForRequestAsync(fixture.Job.Id));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public AppDbContext Db { get; }
        public MediaRequestEntity Request { get; }
        public FulfillmentJobEntity Job { get; }
        public ReleaseBlocklistService Service { get; }

        private Fixture(SqliteConnection connection, AppDbContext db,
            MediaRequestEntity request, FulfillmentJobEntity job)
        {
            _connection = connection;
            Db = db;
            Request = request;
            Job = job;
            Service = new ReleaseBlocklistService(db, NullLogger<ReleaseBlocklistService>.Instance);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var request = new MediaRequestEntity
            {
                MediaId = 46195,
                MediaType = MediaType.TvShow,
                Title = "Monogatari"
            };
            db.MediaRequests.Add(request);
            await db.SaveChangesAsync();
            var job = new FulfillmentJobEntity
            {
                MediaRequestId = request.Id,
                MediaId = request.MediaId,
                MediaType = request.MediaType,
                Title = request.Title
            };
            db.FulfillmentJobs.Add(job);
            await db.SaveChangesAsync();
            return new Fixture(connection, db, request, job);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
