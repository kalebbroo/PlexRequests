using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Download;
using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

public sealed class AcquisitionFoundationTests
{
    [Fact]
    public void Registry_routes_by_protocol_and_rejects_ambiguous_backends()
    {
        var torrent = new StubBackend(AcquisitionProtocol.Torrent);
        var direct = new StubBackend(AcquisitionProtocol.DirectAudio);
        var registry = new AcquisitionBackendRegistry(new IAcquisitionBackend[] { torrent, direct });

        Assert.True(registry.TryGet(AcquisitionProtocol.DirectAudio, out var selected));
        Assert.Same(direct, selected);
        Assert.False(registry.TryGet(AcquisitionProtocol.Usenet, out _));
        Assert.Throws<InvalidOperationException>(() => new AcquisitionBackendRegistry(
            new IAcquisitionBackend[] { torrent, new StubBackend(AcquisitionProtocol.Torrent) }));
    }

    [Fact]
    public async Task Legacy_torrent_state_is_loaded_as_a_torrent_transfer_without_rewriting_it_first()
    {
        var root = Path.Combine(Path.GetTempPath(), $"plexrequests-acquisition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "active-jobs.json");
            await File.WriteAllTextAsync(path,
                "[{\"Job\":{\"Id\":17,\"Title\":\"Legacy\"},\"Torrents\":[{\"TorrentId\":\"abc123\",\"Season\":1,\"Episode\":2,\"IsPack\":false}]}]");
            var store = new JsonJobStateStore(
                Options.Create(new WorkerOptions { StatePath = path }),
                NullLogger<JsonJobStateStore>.Instance);

            var record = Assert.Single(await store.GetAllAsync(CancellationToken.None));
            var transfer = Assert.Single(record.Transfers);
            Assert.Equal("abc123", transfer.TransferId);
            Assert.Equal(AcquisitionProtocol.Torrent, transfer.Protocol);
            Assert.True(record.CoversAllTargets); // old state predates the field and must retain old semantics

            await store.SaveAsync(record with
            {
                CoversAllTargets = false,
                Transfers = new List<TransferItem>
                {
                    transfer with { Protocol = AcquisitionProtocol.DirectAudio, TransferId = "video-id" }
                }
            }, CancellationToken.None);
            var reloaded = Assert.Single(await store.GetAllAsync(CancellationToken.None));
            Assert.Equal(AcquisitionProtocol.DirectAudio, Assert.Single(reloaded.Transfers).Protocol);
            Assert.False(reloaded.CoversAllTargets);

            // The deployed wire names stay compatible with older state files while the in-process model is generic.
            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"Torrents\"", json);
            Assert.Contains("\"TorrentId\"", json);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Candidate_locator_is_typed_and_not_assumed_to_be_a_magnet()
    {
        var candidate = new ReleaseCandidate
        {
            ReleaseName = "Artist - Track",
            Acquisition = new AcquisitionResource
            {
                Protocol = AcquisitionProtocol.DirectAudio,
                Locator = "youtube-video-id",
                SourceId = "youtube-video-id"
            }
        };

        Assert.Equal(AcquisitionProtocol.DirectAudio, candidate.Acquisition.Protocol);
        Assert.Equal("youtube-video-id", candidate.Acquisition.Locator);
        Assert.NotEqual(AcquisitionProtocol.Torrent, candidate.Acquisition.Protocol);
    }

    [Fact]
    public async Task Migration_creates_protocol_columns_for_transfers_and_import_audits()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);

        await db.Database.MigrateAsync();

        Assert.Contains("Protocol", await ColumnsAsync(connection, "FulfillmentTorrents"));
        Assert.Contains("Protocol", await ColumnsAsync(connection, "ImportedFiles"));
        Assert.Contains("Protocol", await ColumnsAsync(connection, "ReleaseBlocklist"));
        Assert.Contains("SourceId", await ColumnsAsync(connection, "ReleaseBlocklist"));
    }

    private static async Task<List<string>> ColumnsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        return columns;
    }

    private sealed class StubBackend(AcquisitionProtocol protocol) : IAcquisitionBackend
    {
        public AcquisitionProtocol Protocol { get; } = protocol;
        public AcquisitionBackendCapabilities Capabilities { get; } = new(false, false, false);
        public Task<string?> EnqueueAsync(AcquisitionRequest request, CancellationToken ct) => Task.FromResult<string?>("id");
        public Task<TransferStatus?> GetStatusAsync(string transferId, CancellationToken ct) => Task.FromResult<TransferStatus?>(null);
        public TransferHealthDecision EvaluateHealth(TransferStatus? status, DateTime addedAt, DateTime? progressChangedAt, DateTime now) => new(TransferVerdict.Wait);
        public Task<bool> RemoveAsync(string transferId, bool removeData, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> SetWantedFilesAsync(string transferId, IReadOnlyList<bool> keep, CancellationToken ct) => Task.FromResult(false);
    }
}
