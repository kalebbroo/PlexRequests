using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlexRequestsHosted.Infrastructure.Catalog;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Background;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

public sealed class ReleaseCatalogTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private PooledDbContextFactory<CatalogDbContext> _factory = null!;
    private ReleaseCatalogService _catalog = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new PooledDbContextFactory<CatalogDbContext>(dbOptions);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        _catalog = new ReleaseCatalogService(
            _factory,
            new ReleaseParser(),
            Options.Create(new CatalogOptions { Enabled = true, MaxBatchSize = 250 }),
            NullLogger<ReleaseCatalogService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Replaying_a_committed_batch_is_a_noop_and_does_not_regress_the_cursor()
    {
        var batch = Batch(1, "batch-one", "cursor-one", Item("row-one", Hex('A')));
        var first = await _catalog.UpsertBatchAsync(batch, CancellationToken.None);
        var replay = await _catalog.UpsertBatchAsync(batch, CancellationToken.None);

        Assert.Equal(1, first.ReleasesInserted);
        Assert.Equal(1, first.SightingsInserted);
        Assert.True(replay.DuplicateBatch);

        await using var db = await _factory.CreateDbContextAsync(CancellationToken.None);
        Assert.Equal(1, await db.Releases.CountAsync(CancellationToken.None));
        Assert.Equal(1, await db.Sightings.CountAsync(CancellationToken.None));
        var checkpoint = await db.Checkpoints.SingleAsync(CancellationToken.None);
        Assert.Equal("cursor-one", checkpoint.Cursor);
        Assert.Equal(1, checkpoint.TotalItemsSeen);
    }

    [Fact]
    public async Task Two_sources_with_the_same_infohash_merge_into_one_release_and_keep_both_sightings()
    {
        await _catalog.UpsertBatchAsync(
            Batch(1, "first", "one", Item("source-a-row", Hex('B'))),
            CancellationToken.None);
        await _catalog.UpsertBatchAsync(
            Batch(2, "second", "two", Item("source-b-row", Hex('b'))),
            CancellationToken.None);

        await using var db = await _factory.CreateDbContextAsync(CancellationToken.None);
        Assert.Equal(1, await db.Releases.CountAsync(CancellationToken.None));
        Assert.Equal(2, await db.Sightings.CountAsync(CancellationToken.None));
        var release = await db.Releases.Include(x => x.Sightings)
            .SingleAsync(CancellationToken.None);
        Assert.Equal(2, release.Sightings.Count);
        Assert.Equal(Hex('b'), release.InfoHash);
    }

    [Fact]
    public async Task A_listing_without_an_infohash_is_kept_for_selective_hydration()
    {
        var unresolved = Item("listing-only", null);
        unresolved.NeedsHydration = true;

        var result = await _catalog.UpsertBatchAsync(
            Batch(7, "listing", "next", unresolved),
            CancellationToken.None);

        Assert.Equal(1, result.UnresolvedSightings);
        await using var db = await _factory.CreateDbContextAsync(CancellationToken.None);
        Assert.Empty(await db.Releases.ToListAsync(CancellationToken.None));
        var sighting = await db.Sightings.SingleAsync(CancellationToken.None);
        Assert.Null(sighting.ReleaseId);
        Assert.Equal(CatalogHydrationState.Pending, sighting.HydrationState);
    }

    [Fact]
    public async Task Pending_hydration_pages_are_source_scoped_cursor_based_and_exclude_resolved_rows()
    {
        var first = Item("pending-one", null);
        first.NeedsHydration = true;
        first.SourceUrl = "https://1337x.to/torrent/1/one/";
        var second = Item("pending-two", null);
        second.NeedsHydration = true;
        second.SourceUrl = "https://1337x.to/torrent/2/two/";
        await _catalog.UpsertBatchAsync(Batch(7, "first", "one", first), CancellationToken.None);
        await _catalog.UpsertBatchAsync(Batch(7, "second", "two", second), CancellationToken.None);
        await _catalog.UpsertBatchAsync(
            Batch(7, "resolved", "three", Item("resolved", Hex('7'))),
            CancellationToken.None);

        var pageOne = await _catalog.GetPendingHydrationAsync(
            7, "source-7", 0, 1, CancellationToken.None);
        var pageTwo = await _catalog.GetPendingHydrationAsync(
            7, "source-7", pageOne.NextCursor, 10, CancellationToken.None);
        var wrongSource = await _catalog.GetPendingHydrationAsync(
            7, "another-source", 0, 10, CancellationToken.None);

        Assert.Equal("pending-one", Assert.Single(pageOne.Items).ExternalId);
        Assert.Equal("pending-two", Assert.Single(pageTwo.Items).ExternalId);
        Assert.True(pageTwo.NextCursor > pageOne.NextCursor);
        Assert.Empty(wrongSource.Items);
    }

    [Fact]
    public async Task A_later_summary_without_a_hash_does_not_detach_an_already_hydrated_sighting()
    {
        await _catalog.UpsertBatchAsync(
            Batch(8, "hydrated", "one", Item("stable-row", Hex('F'))),
            CancellationToken.None);
        var summary = Item("stable-row", null);
        summary.NeedsHydration = true;
        await _catalog.UpsertBatchAsync(
            Batch(8, "summary", "two", summary),
            CancellationToken.None);

        await using var db = await _factory.CreateDbContextAsync(CancellationToken.None);
        var sighting = await db.Sightings.SingleAsync(CancellationToken.None);
        Assert.NotNull(sighting.ReleaseId);
        Assert.Equal(CatalogHydrationState.Hydrated, sighting.HydrationState);
    }

    [Fact]
    public void Base32_and_hex_infohashes_have_one_canonical_form()
    {
        Assert.Equal(new string('0', 40),
            ReleaseCatalogService.NormalizeInfoHash("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", null));
        Assert.Equal(Hex('c'), ReleaseCatalogService.NormalizeInfoHash(null,
            $"magnet:?xt=urn:btih:{Hex('c')}&dn=Example"));
    }

    [Fact]
    public async Task Invalid_items_cannot_advance_a_checkpoint()
    {
        var batch = Batch(1, "bad", "must-not-commit", Item("", Hex('D')));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _catalog.UpsertBatchAsync(batch, CancellationToken.None));

        await using var db = await _factory.CreateDbContextAsync(CancellationToken.None);
        Assert.Empty(await db.Checkpoints.ToListAsync(CancellationToken.None));
        Assert.Empty(await db.BatchReceipts.ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Live_search_write_through_never_changes_a_feed_checkpoint()
    {
        await _catalog.UpsertBatchAsync(
            Batch(3, "feed", "durable-cursor", Item("feed-row", Hex('1'))),
            CancellationToken.None);
        var live = Batch(3, "live", "must-be-ignored", Item("live-row", Hex('2')));
        live.AdvanceCheckpoint = false;
        await _catalog.UpsertBatchAsync(live, CancellationToken.None);

        var checkpoint = await _catalog.GetCheckpointAsync(3, CancellationToken.None);
        Assert.Equal("durable-cursor", checkpoint.Cursor);
        Assert.Equal(0, checkpoint.ConsecutiveFailures);
    }

    [Fact]
    public async Task Stats_include_non_checkpoint_browser_sources_with_real_resolution_counts()
    {
        var listing = Batch(3, "firefox-listing", "ignored", Item("listing", null));
        listing.Source = "1337x (Firefox)";
        listing.AdvanceCheckpoint = false;
        listing.Items[0].NeedsHydration = true;
        await _catalog.UpsertBatchAsync(listing, CancellationToken.None);

        var detail = Batch(3, "firefox-detail", "ignored", Item("detail", Hex('9')));
        detail.Source = "1337x (Firefox)";
        detail.AdvanceCheckpoint = false;
        await _catalog.UpsertBatchAsync(detail, CancellationToken.None);

        var stats = await _catalog.GetStatsAsync(CancellationToken.None);
        var source = Assert.Single(stats.Sources);
        Assert.Equal("1337x (Firefox)", source.Source);
        Assert.Equal(2, source.Sightings);
        Assert.Equal(2, source.TotalItemsSeen);
        Assert.Equal(1, source.TotalItemsResolved);
        Assert.Equal("Closed", source.CircuitState);
        Assert.NotNull(source.LastSuccessAt);
    }

    [Fact]
    public async Task Failure_backoff_is_persisted_and_a_successful_batch_self_heals_the_circuit()
    {
        var failed = await _catalog.ReportFailureAsync(new CatalogFailureDto
        {
            IndexerId = 4,
            Source = "blocked-source",
            Error = "challenge",
            BlockReason = IndexerBlockReason.CloudflareChallenge,
            OccurredAt = new DateTime(2026, 8, 15, 10, 0, 0, DateTimeKind.Utc)
        }, CancellationToken.None);

        Assert.Equal("Open", failed.CircuitState);
        Assert.Equal(new DateTime(2026, 8, 15, 16, 0, 0, DateTimeKind.Utc), failed.NextAttemptAt);

        await _catalog.UpsertBatchAsync(
            Batch(4, "recovery", "healthy-cursor", Item("healthy", Hex('E'))),
            CancellationToken.None);
        var healed = await _catalog.GetCheckpointAsync(4, CancellationToken.None);
        Assert.Equal("Closed", healed.CircuitState);
        Assert.Equal(0, healed.ConsecutiveFailures);
        Assert.Null(healed.NextAttemptAt);
        Assert.Null(healed.LastError);
    }

    [Fact]
    public async Task Catalog_search_rehydrates_a_rankable_candidate_from_the_best_sighting()
    {
        var weak = Item("weak", Hex('3'));
        weak.Seeders = 2;
        await _catalog.UpsertBatchAsync(Batch(11, "weak-batch", "one", weak), CancellationToken.None);
        var strong = Item("strong", Hex('3'));
        strong.Seeders = 42;
        await _catalog.UpsertBatchAsync(Batch(12, "strong-batch", "two", strong), CancellationToken.None);

        var results = await _catalog.SearchAsync(new CatalogQueryDto
        {
            Title = "Example Show",
            MediaType = MediaType.TvShow
        }, CancellationToken.None);

        var candidate = Assert.Single(results);
        Assert.Equal(42, candidate.Seeders);
        Assert.Equal(12, candidate.IndexerId);
        Assert.Equal("source-12", candidate.Source);
        Assert.Equal(1, candidate.Season);
        Assert.Equal(2, candidate.Episode);
        Assert.StartsWith("magnet:?xt=urn:btih:", candidate.Acquisition.Locator);
    }

    [Fact]
    public async Task Catalog_browser_filters_captured_releases_by_source_and_title_tokens()
    {
        var ext = Item("ext-row", Hex('6'));
        ext.ReleaseName = "Rick.and.Morty.S09E01.1080p.WEB-DL.x265-GROUP";
        ext.SourceUrl = "https://ext.to/rick-and-morty-s09e01-10000001/";
        ext.Uploader = "trusted-group";
        ext.Seeders = 73;
        var extBatch = Batch(38, "ext-capture", "ignored", ext);
        extBatch.Source = "ext.to (Firefox)";
        extBatch.AdvanceCheckpoint = false;
        await _catalog.UpsertBatchAsync(extBatch, CancellationToken.None);

        var mirror = Item("mirror-row", Hex('6'));
        mirror.ReleaseName = ext.ReleaseName;
        mirror.Seeders = 4;
        var mirrorBatch = Batch(37, "mirror-capture", "ignored", mirror);
        mirrorBatch.Source = "1337x (Firefox)";
        mirrorBatch.AdvanceCheckpoint = false;
        await _catalog.UpsertBatchAsync(mirrorBatch, CancellationToken.None);

        var results = await _catalog.BrowseAsync(new CatalogBrowseQueryDto
        {
            Search = "Morty Rick",
            IndexerId = 38,
            MediaType = MediaType.TvShow,
            PageSize = 50
        }, CancellationToken.None);

        var release = Assert.Single(results.Items);
        Assert.Equal(1, results.Total);
        Assert.Equal("ext.to (Firefox)", release.Source);
        Assert.Equal(38, release.IndexerId);
        Assert.Equal(73, release.Seeders);
        Assert.Equal(2, release.SightingCount);
        Assert.Equal(9, release.Season);
        Assert.Equal(1, release.Episode);
        Assert.Equal(MediaType.TvShow, release.MediaType);
        Assert.Equal(ext.SourceUrl, release.SourceUrl);
        Assert.Equal("trusted-group", release.Uploader);
    }

    [Fact]
    public async Task Catalog_browser_returns_newest_releases_in_bounded_pages()
    {
        for (var i = 0; i < 12; i++)
        {
            var item = Item($"row-{i}", Hex("0123456789AB"[i]));
            item.ReleaseName = $"Example.Show.S01E{i + 1:00}.1080p.WEB-DL";
            if (i == 11) item.SourceUrl = "javascript:alert(1)";
            var batch = Batch(20, $"batch-{i}", i.ToString(), item);
            batch.ObservedAt = new DateTime(2026, 8, 15, 12, i, 0, DateTimeKind.Utc);
            await _catalog.UpsertBatchAsync(batch, CancellationToken.None);
        }

        var first = await _catalog.BrowseAsync(new CatalogBrowseQueryDto { PageSize = 5 }, CancellationToken.None);
        var second = await _catalog.BrowseAsync(new CatalogBrowseQueryDto { Page = 2, PageSize = 5 }, CancellationToken.None);
        var beyondEnd = await _catalog.BrowseAsync(new CatalogBrowseQueryDto { Page = 99, PageSize = 5 }, CancellationToken.None);

        Assert.Equal(12, first.Total);
        Assert.Equal(10, first.PageSize);
        Assert.Equal(10, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.Contains("S01E12", first.Items[0].ReleaseName);
        Assert.Null(first.Items[0].SourceUrl);
        Assert.Equal(2, beyondEnd.Page);
        Assert.Equal(2, beyondEnd.Items.Count);
    }

    [Fact]
    public async Task Maintenance_prunes_in_batches_but_preserves_hashes_referenced_by_application_history()
    {
        var protectedHash = Hex('4');
        await _catalog.UpsertBatchAsync(Batch(1, "protected", "one", Item("protected", protectedHash)), CancellationToken.None);
        await _catalog.UpsertBatchAsync(Batch(1, "stale", "two", Item("stale", Hex('5'))), CancellationToken.None);
        var unresolved = Item("unresolved", null);
        unresolved.NeedsHydration = true;
        await _catalog.UpsertBatchAsync(Batch(1, "unresolved", "three", unresolved), CancellationToken.None);

        await using var appConnection = new SqliteConnection("Data Source=:memory:");
        await appConnection.OpenAsync();
        var appOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(appConnection).Options;
        var appFactory = new PooledDbContextFactory<AppDbContext>(appOptions);
        await using (var app = await appFactory.CreateDbContextAsync())
        {
            await app.Database.EnsureCreatedAsync();
            app.ReleaseBlocklist.Add(new ReleaseBlocklistEntity { InfoHash = protectedHash });
            await app.SaveChangesAsync();
        }

        var maintenance = new CatalogMaintenanceWorker(
            _factory,
            appFactory,
            Options.Create(new CatalogOptions
            {
                Enabled = true,
                RetentionDays = 90,
                UnresolvedRetentionDays = 14,
                MaxDatabaseSizeMb = 128
            }),
            NullLogger<CatalogMaintenanceWorker>.Instance);
        await maintenance.PruneAsync(
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        await using var catalog = await _factory.CreateDbContextAsync();
        var remaining = await catalog.Releases.Select(x => x.InfoHash).ToListAsync();
        Assert.Equal(new[] { protectedHash }, remaining);
        Assert.DoesNotContain(await catalog.Sightings.ToListAsync(), x => x.ReleaseId == null);
        Assert.Empty(await catalog.BatchReceipts.ToListAsync());
    }

    private static CatalogBatchDto Batch(int indexerId, string batchId, string cursor, params CatalogItemDto[] items) => new()
    {
        IndexerId = indexerId,
        Source = $"source-{indexerId}",
        BatchId = batchId,
        Cursor = cursor,
        ObservedAt = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc),
        Items = items.ToList()
    };

    private static CatalogItemDto Item(string externalId, string? infoHash) => new()
    {
        ExternalId = externalId,
        ReleaseName = "Example.Show.S01E02.1080p.WEB-DL.x265-GROUP",
        InfoHash = infoHash,
        Seeders = 12,
        Leechers = 2,
        SizeBytes = 1_500_000_000
    };

    private static string Hex(char value) => new(value, 40);
}
