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

    [Theory]
    [InlineData("http://lh3.googleusercontent.com/cover")]
    [InlineData("https://user@lh3.googleusercontent.com/cover")]
    [InlineData("https://127.0.0.1/cover")]
    [InlineData("https://i.ytimg.com.evil.test/cover")]
    public void Rejects_untrusted_artwork_urls(string value) =>
        Assert.False(DirectAudioMediaEnricher.TryValidateArtworkUrl(value, out _));

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
}
