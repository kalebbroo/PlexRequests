using System.Text;
using System.Text.Json;

namespace PlexRequests.Downloader.Download;

public sealed record YouTubeMusicTrack(string VideoId, string Title, int DiscNumber, int TrackNumber);

/// <summary>Versioned opaque locator shared only by the YouTube candidate source and direct-audio backend.
/// It carries the exact immutable track list captured with the request, so an album never changes shape
/// halfway through a retry.</summary>
public sealed record YouTubeMusicLocator(int Version, IReadOnlyList<YouTubeMusicTrack> Tracks)
{
    private const string Prefix = "ytmusic:v1:";

    public string Encode()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(this);
        return Prefix + Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? value, out YouTubeMusicLocator locator)
    {
        locator = new YouTubeMusicLocator(1, Array.Empty<YouTubeMusicTrack>());
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(Prefix, StringComparison.Ordinal)
            || value.Length > 262_144) return false;
        try
        {
            var encoded = value[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var decoded = JsonSerializer.Deserialize<YouTubeMusicLocator>(Convert.FromBase64String(encoded));
            if (decoded is null || decoded.Version != 1 || decoded.Tracks.Count is < 1 or > 500
                || decoded.Tracks.Any(x => !IsVideoId(x.VideoId))) return false;
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
