using System.Text.Json;

namespace PlexRequestsHosted.Shared.Releases;

/// <summary>Versioned, opaque identity of one YouTube video frozen for one TV episode. The web app proves the match
/// before the job crosses the VPN boundary; the downloader and the release evaluator only verify that this exact
/// video was frozen for the episode the job still wants.</summary>
public sealed record DirectVideoLocator(
    int Version,
    string VideoId,
    int Season,
    int Episode,
    string Title,
    string? ChannelId = null)
{
    private const string PrefixV1 = "ytvideo:v1:";

    public string Encode()
    {
        if (Version != 1) throw new InvalidOperationException($"Unsupported YouTube video locator version {Version}");
        var json = JsonSerializer.SerializeToUtf8Bytes(this);
        return PrefixV1 + Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? value, out DirectVideoLocator locator)
    {
        locator = new DirectVideoLocator(1, string.Empty, 0, 0, string.Empty);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || !value.StartsWith(PrefixV1, StringComparison.Ordinal))
            return false;
        try
        {
            var encoded = value[PrefixV1.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var decoded = JsonSerializer.Deserialize<DirectVideoLocator>(Convert.FromBase64String(encoded));
            if (decoded is null || decoded.Version != 1 || !IsVideoId(decoded.VideoId)
                || decoded.Season < 1 || decoded.Episode < 1
                || string.IsNullOrWhiteSpace(decoded.Title) || decoded.Title.Length > 512)
                return false;
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
