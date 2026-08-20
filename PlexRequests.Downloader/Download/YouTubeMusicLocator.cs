using System.Text;
using System.Text.Json;

namespace PlexRequests.Downloader.Download;

public sealed record YouTubeMusicTrack(
    string VideoId,
    string Title,
    int DiscNumber,
    int TrackNumber,
    string? Artist = null,
    string? Album = null,
    int TrackCount = 0,
    int? Year = null,
    string? AlbumArtist = null,
    int DiscCount = 0);

/// <summary>Versioned opaque locator shared only by the YouTube candidate source and direct-audio backend.
/// It carries the exact immutable track list captured with the request, so an album never changes shape
/// halfway through a retry.</summary>
public sealed record YouTubeMusicLocator(
    int Version,
    IReadOnlyList<YouTubeMusicTrack> Tracks,
    string? ArtworkUrl = null)
{
    private const string PrefixV1 = "ytmusic:v1:";
    private const string PrefixV2 = "ytmusic:v2:";

    public string Encode()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(this);
        var prefix = Version switch
        {
            1 => PrefixV1,
            2 => PrefixV2,
            _ => throw new InvalidOperationException($"Unsupported YouTube Music locator version {Version}")
        };
        return prefix + Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? value, out YouTubeMusicLocator locator)
    {
        locator = new YouTubeMusicLocator(2, Array.Empty<YouTubeMusicTrack>());
        if (string.IsNullOrWhiteSpace(value) || value.Length > 262_144) return false;
        var expectedVersion = value.StartsWith(PrefixV2, StringComparison.Ordinal) ? 2
            : value.StartsWith(PrefixV1, StringComparison.Ordinal) ? 1 : 0;
        if (expectedVersion == 0) return false;
        try
        {
            var prefixLength = expectedVersion == 2 ? PrefixV2.Length : PrefixV1.Length;
            var encoded = value[prefixLength..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var decoded = JsonSerializer.Deserialize<YouTubeMusicLocator>(Convert.FromBase64String(encoded));
            if (decoded is null || decoded.Version != expectedVersion || decoded.Tracks is null
                || decoded.Tracks.Count is < 1 or > 500
                || decoded.Tracks.Any(x => x is null || !IsVideoId(x.VideoId) || string.IsNullOrWhiteSpace(x.Title))
                || decoded.ArtworkUrl?.Length > 4096) return false;
            locator = decoded;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool IsVideoId(string? value) => value is { Length: 11 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
