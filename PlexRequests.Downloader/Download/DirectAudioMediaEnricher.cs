using System.Net;

namespace PlexRequests.Downloader.Download;

public interface IDirectAudioMediaEnricher
{
    Task<string?> FetchArtworkAsync(string? artworkUrl, string payloadPath, CancellationToken ct);
    Task WriteTagsAsync(string audioPath, YouTubeMusicTrack track, string? artworkPath, CancellationToken ct);
}

/// <summary>Provider-safe artwork fetch plus in-place managed metadata writing. The acquisition backend
/// owns retry/state policy; this component only enriches one deterministic payload and performs no catalog
/// lookup. It supports JPEG/PNG from explicitly trusted catalog artwork hosts and follows only a short,
/// HTTPS, allowlisted redirect chain (required by Cover Art Archive's Internet Archive storage).</summary>
public sealed class DirectAudioMediaEnricher(
    IHttpClientFactory httpClientFactory,
    ILogger<DirectAudioMediaEnricher> logger) : IDirectAudioMediaEnricher
{
    // Cover data is embedded in every track for portability, so keep a strict ceiling to prevent one
    // oversized source image from multiplying into album-level bloat. YouTube's normal 544px covers are
    // far below this limit.
    private const int MaximumArtworkBytes = 2 * 1024 * 1024;
    private const int MaximumRedirects = 4;

    public async Task<string?> FetchArtworkAsync(string? artworkUrl, string payloadPath, CancellationToken ct)
    {
        if (!TryValidateArtworkUrl(artworkUrl, out var uri)) return null;
        foreach (var existing in new[] { "cover.jpg", "cover.png" }.Select(x => Path.Combine(payloadPath, x)))
            if (File.Exists(existing) && new FileInfo(existing).Length > 1024) return existing;

        using var http = httpClientFactory.CreateClient(nameof(DirectAudioMediaEnricher));
        var currentUri = uri;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.Accept.ParseAdd("image/jpeg, image/png;q=0.9");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (IsRedirect(response.StatusCode))
            {
                if (redirects >= MaximumRedirects)
                    throw new HttpRequestException($"Artwork exceeded the {MaximumRedirects}-redirect safety limit");
                var location = response.Headers.Location
                    ?? throw new HttpRequestException("Artwork redirect did not include a location");
                var resolved = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                if (!TryValidateArtworkUrl(resolved.ToString(), out currentUri))
                    throw new InvalidDataException("Artwork redirected to an untrusted location");
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Artwork returned HTTP {(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength is > MaximumArtworkBytes)
                throw new InvalidDataException("Artwork exceeds the 2 MB safety limit");

            var extension = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() switch
            {
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/png" => ".png",
                _ => throw new InvalidDataException("Artwork is not JPEG or PNG")
            };
            Directory.CreateDirectory(payloadPath);
            var destination = Path.Combine(payloadPath, "cover" + extension);
            var temporary = destination + ".tmp";
            try
            {
                var total = 0;
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                                 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        total = checked(total + read);
                        if (total > MaximumArtworkBytes)
                            throw new InvalidDataException("Artwork exceeds the 2 MB safety limit");
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    await output.FlushAsync(ct);
                }
                if (total <= 1024) throw new InvalidDataException("Artwork response is empty or truncated");
                File.Move(temporary, destination, overwrite: true);
                return destination;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) { logger.LogDebug(ex, "Temporary artwork cleanup skipped for {Path}", temporary); }
            }
        }
    }

    public Task WriteTagsAsync(string audioPath, YouTubeMusicTrack track, string? artworkPath,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var media = TagLib.File.Create(audioPath);
        media.Tag.Title = Clean(track.Title, 512);
        if (!string.IsNullOrWhiteSpace(track.Artist))
        {
            var artist = Clean(track.Artist, 512);
            media.Tag.Performers = [artist];
            media.Tag.AlbumArtists = [Clean(track.AlbumArtist ?? track.Artist, 512)];
        }
        if (!string.IsNullOrWhiteSpace(track.Album)) media.Tag.Album = Clean(track.Album, 512);
        media.Tag.Disc = (uint)Math.Max(1, track.DiscNumber);
        if (track.DiscCount > 0) media.Tag.DiscCount = (uint)track.DiscCount;
        media.Tag.Track = (uint)Math.Max(1, track.TrackNumber);
        if (track.TrackCount > 0) media.Tag.TrackCount = (uint)track.TrackCount;
        if (track.Year is > 0 and <= 9999) media.Tag.Year = (uint)track.Year.Value;

        if (!string.IsNullOrWhiteSpace(artworkPath) && File.Exists(artworkPath))
        {
            var mime = Path.GetExtension(artworkPath).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png" : "image/jpeg";
            media.Tag.Pictures =
            [
                new TagLib.Picture
                {
                    Type = TagLib.PictureType.FrontCover,
                    MimeType = mime,
                    Description = "Cover",
                    Data = TagLib.ByteVector.FromPath(artworkPath)
                }
            ];
        }
        media.Save();
        return Task.CompletedTask;
    }

    internal static bool TryValidateArtworkUrl(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096
            || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(parsed.UserInfo)
            || (!parsed.IsDefaultPort && parsed.Port != 443)) return false;
        var host = parsed.IdnHost;
        if (!host.Equals("i.ytimg.com", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("yt3.ggpht.com", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("lh3.googleusercontent.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("coverartarchive.org", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("archive.org", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase)) return false;
        uri = parsed;
        return true;
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static string Clean(string value, int max)
    {
        var clean = value.Trim();
        return clean.Length > max ? clean[..max] : clean;
    }
}
