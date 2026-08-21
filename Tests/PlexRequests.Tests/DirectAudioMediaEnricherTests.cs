using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequests.Downloader.Download;
using Xunit;

namespace PlexRequests.Tests;

public sealed class DirectAudioMediaEnricherTests
{
    // 120 ms of generated silence in a standards-compliant AAC/M4A container. Keeping the fixture inline
    // makes the tag test deterministic and avoids requiring ffmpeg in CI or checking in a binary file.
    private const string SilentM4a =
        "AAAAHGZ0eXBNNEEgAAACAE00QSBpc29taXNvMgAAAvttb292AAAAbG12aGQAAAAAAAAAAAAAAAAAAAPoAAAAeAABAAABAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAAACJXRyYWsAAABcdGtoZAAAAAMAAAAAAAAAAAAAAAEAAAAAAAAAeAAAAAAAAAAAAAAAAQEAAAAAAQAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAACRlZHRzAAAAHGVsc3QAAAAAAAAAAQAAAHgAAAQAAAEAAAAAAZ1tZGlhAAAAIG1kaGQAAAAAAAAAAAAAAAAAAB9AAAADwFXEAAAAAAAtaGRscgAAAAAAAAAAc291bgAAAAAAAAAAAAAAAFNvdW5kSGFuZGxlcgAAAAFIbWluZgAAABBzbWhkAAAAAAAAAAAAAAAkZGluZgAAABxkcmVmAAAAAAAAAAEAAAAMdXJsIAAAAAEAAAEMc3RibAAAAGpzdHNkAAAAAAAAAAEAAABabXA0YQAAAAAAAAABAAAAAAAAAAAAAgAQAAAAAB9AAAAAAAA2ZXNkcwAAAAADgICAJQABAASAgIAXQBUAAAAAAD6AAAAFiwWAgIAFFYhW5QAGgICAAQIAAAAgc3R0cwAAAAAAAAACAAAAAQAABAAAAAABAAADwAAAABxzdHNjAAAAAAAAAAEAAAABAAAAAgAAAAEAAAAUc3RzegAAAAAAAAAWAAAAAgAAABRzdGNvAAAAAAAAAAEAAAMnAAAAGnNncGQBAAAAcm9sbAAAAAIAAAAB//8AAAAcc2JncAAAAAByb2xsAAAAAQAAAAIAAAABAAAAYnVkdGEAAABabWV0YQAAAAAAAAAhaGRscgAAAAAAAAAAbWRpcmFwcGwAAAAAAAAAAAAAAAAtaWxzdAAAACWpdG9vAAAAHWRhdGEAAAABAAAAAExhdmY1OC43Ni4xMDAAAAAIZnJlZQAAADRtZGF03gQATGF2YzU4LjEzNC4xMDAAAjBADt4EAExhdmM1OC4xMzQuMTAwAAIwQA4=";

    [Fact]
    public async Task Writes_plex_grade_text_tags_and_embedded_front_cover_to_m4a()
    {
        var root = NewRoot();
        try
        {
            var audio = Path.Combine(root, "track.m4a");
            var cover = Path.Combine(root, "cover.png");
            await File.WriteAllBytesAsync(audio, Convert.FromBase64String(SilentM4a));
            var coverBytes = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            await File.WriteAllBytesAsync(cover, coverBytes);
            var enricher = new DirectAudioMediaEnricher(new TestHttpClientFactory(),
                NullLogger<DirectAudioMediaEnricher>.Instance);

            await enricher.WriteTagsAsync(audio,
                new YouTubeMusicTrack("abcdefghijk", "Track", 2, 7, "Featured Artist", "Album", 12, 2026,
                    "Album Artist", 2),
                cover, CancellationToken.None);

            using var tagged = TagLib.File.Create(audio);
            Assert.Equal("Track", tagged.Tag.Title);
            Assert.Equal(["Featured Artist"], tagged.Tag.Performers);
            Assert.Equal(["Album Artist"], tagged.Tag.AlbumArtists);
            Assert.Equal("Album", tagged.Tag.Album);
            Assert.Equal((uint)2, tagged.Tag.Disc);
            Assert.Equal((uint)2, tagged.Tag.DiscCount);
            Assert.Equal((uint)7, tagged.Tag.Track);
            Assert.Equal((uint)12, tagged.Tag.TrackCount);
            Assert.Equal((uint)2026, tagged.Tag.Year);
            var picture = Assert.Single(tagged.Tag.Pictures);
            // MP4's covr atom does not persist an ID3-style picture type; the single embedded image is
            // nevertheless the cover Plex consumes.
            Assert.Equal("image/png", picture.MimeType);
            Assert.Equal(coverBytes.Length, picture.Data.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Artwork_fetch_is_host_limited_size_bounded_and_atomic()
    {
        var root = NewRoot();
        try
        {
            var handler = new StaticImageHandler(Enumerable.Repeat((byte)0x42, 2048).ToArray(), "image/jpeg");
            var factory = new TestHttpClientFactory(handler);
            var enricher = new DirectAudioMediaEnricher(factory, NullLogger<DirectAudioMediaEnricher>.Instance);

            var path = await enricher.FetchArtworkAsync("https://lh3.googleusercontent.com/album-cover",
                root, CancellationToken.None);

            Assert.Equal(Path.Combine(root, "cover.jpg"), path);
            Assert.Equal(2048, new FileInfo(path!).Length);
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(1, factory.Created);

            Assert.Null(await enricher.FetchArtworkAsync("https://lh3.googleusercontent.com.evil.test/cover",
                root, CancellationToken.None));
            Assert.Equal(1, factory.Created);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Cover_art_archive_redirect_chain_is_validated_at_every_hop()
    {
        var root = NewRoot();
        try
        {
            var handler = new CoverArtRedirectHandler();
            var enricher = new DirectAudioMediaEnricher(new TestHttpClientFactory(handler),
                NullLogger<DirectAudioMediaEnricher>.Instance);

            var path = await enricher.FetchArtworkAsync(
                "https://coverartarchive.org/release-group/release-id/front-500",
                root, CancellationToken.None);

            Assert.Equal(Path.Combine(root, "cover.jpg"), path);
            Assert.Equal(2048, new FileInfo(path!).Length);
            Assert.Equal(new[] { "coverartarchive.org", "archive.org", "dn.test.archive.org" },
                handler.Requests.Select(x => x.Host));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Trusted_artwork_source_cannot_redirect_to_an_untrusted_host()
    {
        var root = NewRoot();
        try
        {
            var handler = new SingleRedirectHandler("https://archive.org.evil.test/cover.jpg");
            var enricher = new DirectAudioMediaEnricher(new TestHttpClientFactory(handler),
                NullLogger<DirectAudioMediaEnricher>.Instance);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => enricher.FetchArtworkAsync(
                "https://coverartarchive.org/release-group/release-id/front-500",
                root, CancellationToken.None));

            Assert.Contains("untrusted", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(handler.Requests);
            Assert.Empty(Directory.EnumerateFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Trusted_artwork_redirect_loop_is_bounded()
    {
        var root = NewRoot();
        try
        {
            var handler = new SingleRedirectHandler("https://coverartarchive.org/loop");
            var enricher = new DirectAudioMediaEnricher(new TestHttpClientFactory(handler),
                NullLogger<DirectAudioMediaEnricher>.Instance);

            var error = await Assert.ThrowsAsync<HttpRequestException>(() => enricher.FetchArtworkAsync(
                "https://coverartarchive.org/loop", root, CancellationToken.None));

            Assert.Contains("redirect safety limit", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(5, handler.Requests.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("http://lh3.googleusercontent.com/cover")]
    [InlineData("https://user@lh3.googleusercontent.com/cover")]
    [InlineData("https://127.0.0.1/cover")]
    [InlineData("https://i.ytimg.com.evil.test/cover")]
    [InlineData("https://coverartarchive.org.evil.test/cover")]
    [InlineData("https://archive.org.evil.test/cover")]
    public void Rejects_untrusted_artwork_urls(string value) =>
        Assert.False(DirectAudioMediaEnricher.TryValidateArtworkUrl(value, out _));

    [Theory]
    [InlineData("https://coverartarchive.org/release/abc/front-500")]
    [InlineData("https://archive.org/download/mbid/file.jpg")]
    [InlineData("https://dn.example.archive.org/file.jpg")]
    public void Accepts_known_catalog_artwork_hosts(string value) =>
        Assert.True(DirectAudioMediaEnricher.TryValidateArtworkUrl(value, out _));

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plexrequests-tags-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
    {
        public int Created { get; private set; }
        public HttpClient CreateClient(string name)
        {
            Created++;
            return handler is null
                ? throw new InvalidOperationException("HTTP was not expected")
                : new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class StaticImageHandler(byte[] bytes, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class CoverArtRedirectHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            if (uri.Host == "coverartarchive.org")
                return Task.FromResult(Redirect(HttpStatusCode.TemporaryRedirect,
                    "https://archive.org/download/mbid/file.jpg"));
            if (uri.Host == "archive.org")
                return Task.FromResult(Redirect(HttpStatusCode.Found,
                    "https://dn.test.archive.org/items/mbid/file.jpg"));

            var content = new ByteArrayContent(Enumerable.Repeat((byte)0x42, 2048).ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class SingleRedirectHandler(string location) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(Redirect(HttpStatusCode.TemporaryRedirect, location));
        }
    }

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location);
        return response;
    }
}
