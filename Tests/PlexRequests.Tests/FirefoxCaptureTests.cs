using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.IO.Compression;
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
            app.Indexers.AddRange(
                new IndexerEntity
                {
                    Id = 37,
                    Name = "1337x",
                    Implementation = "1337x",
                    IsBuiltIn = true
                },
                new IndexerEntity
                {
                    Id = 38,
                    Name = "ext.to",
                    Implementation = "ext.to",
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
            new FirefoxExtensionInfo("1.10.0"),
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
        Assert.Equal("1337x", paired.Implementation);
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
        Assert.True((await _capture.GetAdminStatusAsync(37)).Devices.Single().UpdateAvailable);
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
        Assert.Equal("1.2.0", device.ExtensionVersion);
        Assert.Equal("1.10.0", status.CurrentExtensionVersion);
        Assert.True(device.UpdateAvailable);
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

        var extPage = Batch("wrong-source", "listing", Item());
        extPage.PageUrl = "https://ext.to/browse/tv/";
        extPage.Items[0].ExternalId = "extto:torrent:1";
        extPage.Items[0].SourceUrl = "https://ext.to/torrent/1/example";
        await Assert.ThrowsAsync<ArgumentException>(() => _capture.IngestAsync(token, extPage));

        await using var catalog = await _catalogFactory.CreateDbContextAsync();
        Assert.Empty(await catalog.Sightings.ToListAsync());
    }

    [Fact]
    public async Task ExtTo_pairing_ingests_only_ExtTo_pages_into_its_own_catalog_source()
    {
        var token = await PairAsync(38);
        var batch = Batch("ext-listing", "listing", new CatalogItemDto
        {
            ExternalId = "extto:torrent:10000002",
            ReleaseName = "Example.Show.S03E04.1080p.WEB-DL",
            SourceUrl = "https://ext.to/example-show-s03e04-10000002/",
            MagnetUri = $"magnet:?xt=urn:btih:{new string('B', 40)}&dn=Example.Show",
            Seeders = 1200
        });
        batch.PageUrl = "https://ext.to/browse/tv/";

        var result = await _capture.IngestAsync(token, batch);

        Assert.Equal(1, result.ReleasesInserted);
        var connection = await _capture.GetConnectionAsync(token);
        Assert.Equal("ext.to", connection!.Implementation);
        Assert.Equal("1.10.0", connection.CurrentExtensionVersion);
        await using var catalog = await _catalogFactory.CreateDbContextAsync();
        var sighting = await catalog.Sightings.SingleAsync();
        Assert.Equal(38, sighting.IndexerId);
        Assert.Equal("ext.to (Firefox)", sighting.Source);

        batch.BatchId = "cross-source";
        batch.PageUrl = "https://1337x.to/search/example/1/";
        await Assert.ThrowsAsync<ArgumentException>(() => _capture.IngestAsync(token, batch));
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
    public async Task Heartbeat_persists_only_the_authenticated_sources_queue_health()
    {
        var token = await PairAsync(38);
        var pausedUntil = _clock.GetUtcNow().UtcDateTime.AddMinutes(15);

        Assert.NotNull(await _capture.RecordHeartbeatAsync(token, new FirefoxCaptureHeartbeatDto
        {
            CaptureEnabled = true,
            QueuedUploads = 2,
            FailedUploads = 1,
            PendingDetails = 37,
            AttentionDetails = 4,
            HydrationPausedUntil = pausedUntil,
            ExtensionVersion = "1.10.0"
        }));

        var status = await _capture.GetAdminStatusAsync(38);
        var device = Assert.Single(status.Devices);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, device.LastHeartbeatAt);
        Assert.Equal(device.LastHeartbeatAt, device.LastSeenAt);
        Assert.True(device.CaptureEnabled);
        Assert.Equal(2, device.QueuedUploads);
        Assert.Equal(1, device.FailedUploads);
        Assert.Equal(37, device.PendingDetails);
        Assert.Equal(4, device.AttentionDetails);
        Assert.Equal(pausedUntil, device.HydrationPausedUntil);
        Assert.Equal("1.10.0", device.ExtensionVersion);
        Assert.False(device.UpdateAvailable);

        var otherSource = await _capture.GetAdminStatusAsync(37);
        Assert.Empty(otherSource.Devices);
    }

    [Fact]
    public async Task Heartbeat_rejects_impossible_counts_and_revoked_tokens()
    {
        var token = await PairAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _capture.RecordHeartbeatAsync(token,
            new FirefoxCaptureHeartbeatDto { QueuedUploads = -1 }));

        var device = Assert.Single((await _capture.GetAdminStatusAsync(37)).Devices);
        Assert.True(await _capture.RevokeDeviceAsync(device.Id));
        Assert.Null(await _capture.RecordHeartbeatAsync(token, new FirefoxCaptureHeartbeatDto()));
    }

    [Fact]
    public async Task Active_device_lease_renews_near_expiry_and_returns_the_authoritative_expiration()
    {
        var token = await PairAsync();
        var originalExpiry = Assert.Single((await _capture.GetAdminStatusAsync(37)).Devices).ExpiresAt;

        _clock.Advance(TimeSpan.FromDays(59));
        var beforeWindow = await _capture.RecordHeartbeatAsync(token, new FirefoxCaptureHeartbeatDto());
        Assert.NotNull(beforeWindow);
        Assert.Equal(originalExpiry, beforeWindow.ExpiresAt);

        _clock.Advance(TimeSpan.FromDays(2));
        var renewed = await _capture.RecordHeartbeatAsync(token, new FirefoxCaptureHeartbeatDto());
        Assert.NotNull(renewed);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddDays(90), renewed.ExpiresAt);
        Assert.Equal(renewed.ExpiresAt, Assert.Single((await _capture.GetAdminStatusAsync(37)).Devices).ExpiresAt);
    }

    [Fact]
    public async Task Expired_device_lease_cannot_be_revived_by_heartbeat_or_status()
    {
        var token = await PairAsync();
        _clock.Advance(TimeSpan.FromDays(91));

        Assert.Null(await _capture.RecordHeartbeatAsync(token, new FirefoxCaptureHeartbeatDto()));
        Assert.Null(await _capture.GetConnectionAsync(token));
        var device = Assert.Single((await _capture.GetAdminStatusAsync(37)).Devices);
        Assert.False(device.IsActive);
    }

    [Fact]
    public async Task Durable_backlog_is_authenticated_source_scoped_and_limited_to_recoverable_adapters()
    {
        var token = await PairAsync();
        await _capture.IngestAsync(token, Batch("recoverable", "listing", Item()));

        var page = await _capture.GetPendingDetailsAsync(token, 0, 250);
        var item = Assert.Single(page!.Items);
        Assert.Equal("1337x:torrent:1", item.ExternalId);
        Assert.True(item.NeedsHydration);
        Assert.Null(await _capture.GetPendingDetailsAsync("invalid-token", 0, 250));

        var extToken = await PairAsync(38);
        var extBatch = Batch("ext-pending", "listing", new CatalogItemDto
        {
            ExternalId = "extto:torrent:10000002",
            ReleaseName = "Example.Show.S01E02.1080p.WEB-DL",
            SourceUrl = "https://ext.to/example-show-s01e02-10000002/",
            NeedsHydration = true
        });
        extBatch.PageUrl = "https://ext.to/browse/tv/";
        await _capture.IngestAsync(extToken, extBatch);

        Assert.Empty((await _capture.GetPendingDetailsAsync(extToken, 0, 250))!.Items);
    }

    [Fact]
    public async Task Indexer_rename_does_not_orphan_the_devices_existing_hydration_backlog()
    {
        var token = await PairAsync();
        await _capture.IngestAsync(token, Batch("before-rename", "listing", Item()));

        await using (var app = await _appFactory.CreateDbContextAsync())
        {
            var indexer = await app.Indexers.SingleAsync(x => x.Id == 37);
            indexer.Name = "Living Room 1337x";
            await app.SaveChangesAsync();
        }

        var connection = await _capture.GetConnectionAsync(token);
        var pending = await _capture.GetPendingDetailsAsync(token, 0, 250);

        Assert.Equal("1337x (Firefox)", connection!.Source);
        Assert.Equal("1337x:torrent:1", Assert.Single(pending!.Items).ExternalId);
        await using var catalog = await _catalogFactory.CreateDbContextAsync();
        Assert.Equal("1337x (Firefox)", (await catalog.Sightings.SingleAsync()).Source);
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

    [Fact]
    public void Downloadable_xpi_is_nonempty_and_contains_the_hydration_runtime()
    {
        var root = Path.Combine(Path.GetTempPath(), $"plexrequests-firefox-{Guid.NewGuid():N}");
        var extension = Path.Combine(root, "BrowserExtension", "firefox");
        try
        {
            Directory.CreateDirectory(extension);
            File.WriteAllText(Path.Combine(extension, "manifest.json"), """
                {
                  "version": "1.10.0",
                  "background": { "scripts": ["sources.js", "hydration.js", "telemetry.js", "capture-queue.js", "background.js"] },
                  "content_scripts": [{ "js": ["sources.js", "capture-queue.js", "parser.js", "content.js"] }],
                  "action": { "default_popup": "popup/popup.html", "default_icon": "icons/capture.svg" },
                  "icons": { "48": "icons/capture.svg" }
                }
                """);
            foreach (var file in new[]
            {
                "background.js", "capture-queue.js", "content.js", "hydration.js", "parser.js", "sources.js", "telemetry.js",
                "popup/popup.html", "popup/popup.css", "popup/popup.js", "icons/capture.svg", "README.md", "future-runtime.js",
                "tests/not-packaged.test.js"
            })
            {
                var path = Path.Combine(extension, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file);
            }

            var xpi = FirefoxExtensionArchive.Create(root);
            Assert.True(xpi.Length > 4);
            Assert.Equal((byte)'P', xpi[0]);
            Assert.Equal((byte)'K', xpi[1]);

            using var stream = new MemoryStream(xpi);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            Assert.Contains(archive.Entries, entry => entry.FullName == "hydration.js");
            Assert.Contains(archive.Entries, entry => entry.FullName == "sources.js");
            Assert.Contains(archive.Entries, entry => entry.FullName == "telemetry.js");
            Assert.Contains(archive.Entries, entry => entry.FullName == "capture-queue.js");
            Assert.Contains(archive.Entries, entry => entry.FullName == "future-runtime.js");
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("tests/", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Downloadable_archive_rejects_a_missing_manifest_asset_before_sending_a_broken_zip()
    {
        var root = Path.Combine(Path.GetTempPath(), $"plexrequests-firefox-missing-{Guid.NewGuid():N}");
        var extension = Path.Combine(root, "BrowserExtension", "firefox");
        try
        {
            Directory.CreateDirectory(extension);
            File.WriteAllText(Path.Combine(extension, "manifest.json"), """
                { "version": "1.10.0", "background": { "scripts": ["missing-runtime.js"] } }
                """);

            var error = Assert.Throws<FileNotFoundException>(() => FirefoxExtensionArchive.Create(root));

            Assert.Contains("missing-runtime.js", error.Message);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Downloadable_archive_rejects_manifest_asset_paths_outside_the_extension()
    {
        var root = Path.Combine(Path.GetTempPath(), $"plexrequests-firefox-path-{Guid.NewGuid():N}");
        var extension = Path.Combine(root, "BrowserExtension", "firefox");
        try
        {
            Directory.CreateDirectory(extension);
            File.WriteAllText(Path.Combine(root, "outside.js"), "outside");
            File.WriteAllText(Path.Combine(extension, "manifest.json"), """
                { "version": "1.10.0", "background": { "scripts": ["../../outside.js"] } }
                """);

            Assert.Throws<InvalidDataException>(() => FirefoxExtensionArchive.Create(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Packaged_manifest_is_the_single_source_for_extension_version_health()
    {
        var root = Path.Combine(Path.GetTempPath(), $"plexrequests-firefox-version-{Guid.NewGuid():N}");
        var extension = Path.Combine(root, "BrowserExtension", "firefox");
        try
        {
            Directory.CreateDirectory(extension);
            File.WriteAllText(Path.Combine(extension, "manifest.json"), "{\"version\":\"1.10.0\"}");

            var info = FirefoxExtensionArchive.Inspect(root);

            Assert.Equal("1.10.0", info.CurrentVersion);
            Assert.True(info.IsUpdateAvailable("1.5.9"));
            Assert.True(info.IsUpdateAvailable("1.6.0"));
            Assert.True(info.IsUpdateAvailable("1.7.0"));
            Assert.True(info.IsUpdateAvailable("1.8.0"));
            Assert.True(info.IsUpdateAvailable("1.9.0"));
            Assert.False(info.IsUpdateAvailable("1.10.0"));
            Assert.True(info.IsUpdateAvailable("1.6"));
            Assert.False(info.IsUpdateAvailable("1.11.0"));
            Assert.True(info.IsUpdateAvailable("unknown"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private async Task<string> PairAsync(int indexerId = 37)
    {
        var pairing = await _capture.CreatePairingAsync(indexerId);
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
        ExtensionVersion = "1.2.0",
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
