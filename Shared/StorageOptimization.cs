namespace PlexRequestsHosted.Shared;

/// <summary>Normalizes the inconsistent codec names emitted by release titles, Plex, and MediaInfo.</summary>
public static class VideoCodecPolicy
{
    public static string? Normalize(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return null;
        var value = new string(codec.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (value is "hevc" or "h265" or "x265" or "hev1" or "hvc1") return "hevc";
        if (value is "avc" or "avc1" or "h264" or "x264") return "h264";
        if (value is "av1" or "av01") return "av1";
        return value.Length == 0 ? null : value;
    }

    public static bool Matches(string? actual, string? required) =>
        Normalize(actual) is { } normalized && normalized == Normalize(required);

    public static string Display(string? codec) => Normalize(codec) switch
    {
        "hevc" => "HEVC (H.265)",
        "h264" => "H.264",
        "av1" => "AV1",
        { } value => value.ToUpperInvariant(),
        _ => "an unknown codec"
    };
}

/// <summary>Classifies cropped theatrical frames by either dimension. A 3840×1600 encode is still 4K,
/// just as a 1920×800 encode is still 1080p; vertical height alone would incorrectly downgrade both.</summary>
public static class VideoResolutionPolicy
{
    public static Enums.Quality FromDimensions(int? width, int? height)
    {
        var w = width ?? 0;
        var h = height ?? 0;
        if (w >= 7600 || h >= 4000) return Enums.Quality.UHD8K;
        if (w >= 3800 || h >= 2000) return Enums.Quality.UHD4K;
        if (w >= 1900 || h >= 1000) return Enums.Quality.FullHD;
        if (w >= 1260 || h >= 700) return Enums.Quality.HD;
        if (w >= 700 || h >= 450) return Enums.Quality.SD;
        return Enums.Quality.Any;
    }
}
