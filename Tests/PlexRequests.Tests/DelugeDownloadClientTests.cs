using System.Net;
using System.Text;
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

    private sealed class ScriptedHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            Assert.NotEmpty(_responses);
            var json = _responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
