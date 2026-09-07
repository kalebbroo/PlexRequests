using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Download;
using Xunit;

namespace PlexRequests.Tests;

public sealed class DelugeDownloadClientTests
{
    [Fact]
    public async Task Removing_an_already_absent_torrent_is_idempotent_success()
    {
        var handler = new ScriptedHandler(
            "{\"id\":1,\"result\":true,\"error\":null}",
            "{\"id\":2,\"result\":null,\"error\":{\"message\":\"Failure: <class 'deluge.error.InvalidTorrentError'>: torrent_id abc not in session.\"}}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://deluge.test") };
        var client = new DelugeDownloadClient(
            http,
            Options.Create(new DelugeOptions { Password = "test-password" }),
            NullLogger<DelugeDownloadClient>.Instance);

        Assert.True(await client.RemoveAsync("abc", removeData: true, CancellationToken.None));
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task PrefetchedMetadataIsHashVerifiedAndEnqueuedWithAtomicFilePriorities()
    {
        var info = BuildInfo(
            ("01 - Arc", "[Group] Show - 01.mkv", 100),
            ("Extras", "sample.mkv", 50));
        var hash = Convert.ToHexString(SHA1.HashData(info)).ToLowerInvariant();
        var magnet = $"magnet:?xt=urn:btih:{hash}&tr={Uri.EscapeDataString("udp://tracker.example:80/announce")}";
        var prefetch = JsonSerializer.Serialize(new
        {
            id = 2,
            result = new object[] { hash, Convert.ToBase64String(info) },
            error = (object?)null
        });
        var handler = new ScriptedHandler(
            "{\"id\":1,\"result\":true,\"error\":null}",
            prefetch,
            "{\"id\":3,\"result\":true,\"error\":null}",
            $"{{\"id\":4,\"result\":\"{hash}\",\"error\":null}}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://deluge.test") };
        var client = new DelugeDownloadClient(http,
            Options.Create(new DelugeOptions { Password = "test-password" }),
            NullLogger<DelugeDownloadClient>.Instance);

        var manifest = Assert.IsType<AcquisitionManifest>(
            await client.GetMagnetManifestAsync(magnet, CancellationToken.None));
        Assert.Equal(["01 - Arc/[Group] Show - 01.mkv", "Extras/sample.mkv"],
            manifest.Files.Select(file => file.Path).ToList());
        Assert.Equal([100L, 50L], manifest.Files.Select(file => file.SizeBytes).ToList());

        var added = await client.AddMagnetAsync(magnet, null, CancellationToken.None,
            manifest, [true, false]);

        Assert.Equal(hash, added);
        using var request = JsonDocument.Parse(handler.RequestBodies[3]);
        Assert.Equal("core.add_torrent_file", request.RootElement.GetProperty("method").GetString());
        var parameters = request.RootElement.GetProperty("params");
        Assert.Equal([4, 0], parameters[2].GetProperty("file_priorities")
            .EnumerateArray().Select(value => value.GetInt32()).ToList());
        var torrentBytes = Convert.FromBase64String(parameters[1].GetString()!);
        Assert.True(torrentBytes.AsSpan().IndexOf(info) >= 0);
    }

    [Fact]
    public void MetadataWhoseInfoHashDiffersFromMagnetIsRejected()
    {
        var info = BuildInfo(("01 - Arc", "Show - 01.mkv", 100));
        var wrongHash = new string('a', 40);

        Assert.Throws<InvalidDataException>(() => TorrentMetadata.ParseInfo(
            info, $"magnet:?xt=urn:btih:{wrongHash}"));
    }

    [Fact]
    public async Task PreflightDoesNotAdoptAnInSessionTorrentWithAnotherJobsSelection()
    {
        var hash = new string('a', 40);
        var handler = new ScriptedHandler(
            "{\"id\":1,\"result\":true,\"error\":null}",
            $"{{\"id\":2,\"result\":null,\"error\":{{\"message\":\"Torrent already in session ({hash}).\"}}}}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://deluge.test") };
        var client = new DelugeDownloadClient(http,
            Options.Create(new DelugeOptions { Password = "test-password" }),
            NullLogger<DelugeDownloadClient>.Instance);

        var manifest = await client.GetMagnetManifestAsync(
            $"magnet:?xt=urn:btih:{hash}", CancellationToken.None);

        Assert.Null(manifest);
        Assert.Equal(2, handler.CallCount);
    }

    private static byte[] BuildInfo(params (string Folder, string Name, long Length)[] files)
    {
        using var stream = new MemoryStream();
        WriteAscii(stream, "d5:filesl");
        foreach (var file in files)
        {
            WriteAscii(stream, $"d6:lengthi{file.Length}e4:pathl");
            WriteString(stream, file.Folder);
            WriteString(stream, file.Name);
            WriteAscii(stream, "ee");
        }
        WriteAscii(stream, "e4:name4:Showe");
        return stream.ToArray();
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteAscii(stream, $"{bytes.Length}:");
        stream.Write(bytes);
    }

    private static void WriteAscii(Stream stream, string value) =>
        stream.Write(Encoding.ASCII.GetBytes(value));

    private sealed class ScriptedHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.NotEmpty(_responses);
            var json = _responses.Dequeue();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
