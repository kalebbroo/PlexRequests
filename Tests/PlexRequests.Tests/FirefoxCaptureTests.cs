using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlexRequestsHosted.Infrastructure.Capture;
using PlexRequestsHosted.Infrastructure.Catalog;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

public sealed class FirefoxCaptureTests : IAsyncLifetime
{
    private readonly SqliteConnection _appConnection = new("Data Source=:memory:");
    private readonly SqliteConnection _catalogConnection = new("Data Source=:memory:");
    private PooledDbContextFactory<AppDbContext> _appFactory = null!;
    private PooledDbContextFactory<CatalogDbContext> _catalogFactory = null!;
    private FirefoxCaptureService _capture = null!;
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 8, 16, 17, 30, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        await _appConnection.OpenAsync();
        await _catalogConnection.OpenAsync();
        _appFactory = new PooledDbContextFactory<AppDbContext>(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_appConnection).Options);
        _catalogFactory = new PooledDbContextFactory<CatalogDbContext>(
            new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite(_catalogConnection).Options);

        await using (var app = await _appFactory.CreateDbContextAsync())
        {
            await app.Database.EnsureCreatedAsync();
            app.Indexers.Add(new IndexerEntity
            {
                Id = 37,
                Name = "1337x",
                Implementation = "1337x",
                IsBuiltIn = true
            });
            await app.SaveChangesAsync();
        }
        await using (var catalogDb = await _catalogFactory.CreateDbContextAsync())
            await catalogDb.Database.EnsureCreatedAsync();

        var catalogOptions = Options.Create(new CatalogOptions { Enabled = true, MaxBatchSize = 250 });
        var catalog = new ReleaseCatalogService(
            _catalogFactory,
            new ReleaseParser(),
            catalogOptions,
            NullLogger<ReleaseCatalogService>.Instance);
        _capture = new FirefoxCaptureService(
            _appFactory,
            catalog,
            Options.Create(new FirefoxCaptureOptions { Enabled = true }),
            catalogOptions,
            _clock,
            NullLogger<FirefoxCaptureService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _appConnection.DisposeAsync();
        await _catalogConnection.DisposeAsync();
    }

    [Fact]
    public async Task Pairing_is_single_use_and_only_token_hashes_are_persisted()
    {
        var pairing = await _capture.CreatePairingAsync(37);
        var paired = await _capture.RedeemPairingAsync(new FirefoxCapturePairRequestDto
        {
            PairingCode = pairing.Code.ToLowerInvariant().Replace("-", " "),
            DeviceName = "Living room Firefox",
            ExtensionVersion = "1.0.0"
        });

        Assert.NotNull(paired);
        Assert.StartsWith("prfc_", paired.Token);
        Assert.Null(await _capture.RedeemPairingAsync(new FirefoxCapturePairRequestDto
        {
            PairingCode = pairing.Code,
            DeviceName = "Replay"
        }));

        await using var db = await _appFactory.CreateDbContextAsync();
        var storedPairing = await db.FirefoxCapturePairings.SingleAsync();
        var storedDevice = await db.FirefoxCaptureDevices.SingleAsync();
        Assert.NotEqual(pairing.Code, storedPairing.CodeHash);
        Assert.NotEqual(paired.Token, storedDevice.TokenHash);
        Assert.Equal(64, storedPairing.CodeHash.Length);
        Assert.Equal(64, storedDevice.TokenHash.Length);
    }

    [Fact]
    public async Task Listing_capture_is_hydrated_by_the_later_detail_page_and_replays_are_harmless()
    {
        var token = await PairAsync();
        var listing = await _capture.IngestAsync(token, Batch("listing-one", "listing", new CatalogItemDto
        {
            ExternalId = "1337x:torrent:7654321",
            ReleaseName = "Example.Show.S01E02.1080p.WEB-DL.x265-GROUP",
            SourceUrl = "https://1337x.to/torrent/7654321/example/",
            Seeders = 41,
            NeedsHydration = true
        }));
        Assert.Equal(1, listing.UnresolvedSightings);

        var detailBatch = Batch("detail-one", "detail", new CatalogItemDto
        {
            ExternalId = "1337x:torrent:7654321",
            ReleaseName = "Example.Show.S01E02.1080p.WEB-DL.x265-GROUP",
            SourceUrl = "https://1337x.to/torrent/7654321/example/",
            MagnetUri = $"magnet:?xt=urn:btih:{new string('A', 40)}&dn=Example.Show",
            Seeders = 43
        });
        var detail = await _capture.IngestAsync(token, detailBatch);
        var replay = await _capture.IngestAsync(token, detailBatch);

        Assert.Equal(1, detail.ReleasesInserted);
        Assert.True(replay.DuplicateBatch);
        await using var catalog = await _catalogFactory.CreateDbContextAsync();
        var sighting = await catalog.Sightings.Include(x => x.Release).SingleAsync();
        Assert.NotNull(sighting.Release);
        Assert.Equal("1337x (Firefox)", sighting.Source);
        Assert.Equal(43, sighting.Seeders);

        var status = await _capture.GetAdminStatusAsync(37);
        var device = Assert.Single(status.Devices);
        Assert.Equal(2, device.BatchesReceived);
        Assert.Equal(2, device.ItemsReceived);
        Assert.Equal(1, device.LastParserVersion);
    }

    [Fact]
    public async Task A_capture_token_cannot_submit_pages_or_item_links_from_other_hosts()
    {
        var token = await PairAsync();
        var badPage = Batch("bad-page", "listing", Item());
        badPage.PageUrl = "https://example.com/search";
        await Assert.ThrowsAsync<ArgumentException>(() => _capture.IngestAsync(token, badPage));

        var badItem = Batch("bad-item", "listing", Item());
        badItem.Items[0].SourceUrl = "https://example.com/torrent/1";
        await Assert.ThrowsAsync<ArgumentException>(() => _capture.IngestAsync(token, badItem));

        await using var catalog = await _catalogFactory.CreateDbContextAsync();
        Assert.Empty(await catalog.Sightings.ToListAsync());
    }

    [Fact]
    public async Task Revocation_immediately_stops_an_existing_device_token()
    {
        var token = await PairAsync();
        var status = await _capture.GetAdminStatusAsync(37);
        var device = Assert.Single(status.Devices);
        Assert.True(await _capture.RevokeDeviceAsync(device.Id));
        Assert.Null(await _capture.GetConnectionAsync(token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _capture.IngestAsync(token, Batch("after-revoke", "listing", Item())));
    }

    [Fact]
    public async Task Expired_pairing_codes_cannot_be_redeemed()
    {
        var pairing = await _capture.CreatePairingAsync(37);
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(await _capture.RedeemPairingAsync(new FirefoxCapturePairRequestDto
        {
            PairingCode = pairing.Code,
            DeviceName = "Too late"
        }));
    }

    [Fact]
    public async Task Malformed_items_are_rejected_without_a_server_error()
    {
        var token = await PairAsync();
        var item = Item();
        item.ReleaseName = null!;
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _capture.IngestAsync(token, Batch("missing-name", "listing", item)));
    }

    private async Task<string> PairAsync()
    {
        var pairing = await _capture.CreatePairingAsync(37);
        var response = await _capture.RedeemPairingAsync(new FirefoxCapturePairRequestDto
        {
            PairingCode = pairing.Code,
            DeviceName = "Test Firefox",
            ExtensionVersion = "1.0.0"
        });
        return response!.Token;
    }

    private static FirefoxCaptureBatchDto Batch(string id, string pageType, CatalogItemDto item) => new()
    {
        BatchId = id,
        PageUrl = "https://1337x.to/search/example/1/",
        PageType = pageType,
        ParserVersion = 1,
        CapturedAt = new DateTime(2026, 8, 16, 17, 30, 0, DateTimeKind.Utc),
        Items = [item]
    };

    private static CatalogItemDto Item() => new()
    {
        ExternalId = "1337x:torrent:1",
        ReleaseName = "Example.Movie.2026.1080p.WEB-DL.x265-GROUP",
        SourceUrl = "https://1337x.to/torrent/1/example/",
        NeedsHydration = true
    };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
