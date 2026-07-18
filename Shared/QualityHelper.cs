using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared;

/// <summary>
/// Conversions between a raw vertical resolution (pixel height parsed from a release, e.g. 1080) and the
/// <see cref="Quality"/> tier enum. The enum's integer values ARE the canonical tier heights
/// (SD=480, HD=720, FullHD=1080, UHD4K=2160, UHD8K=4320), so a height snaps DOWN to the nearest tier it
/// satisfies — a 1088p or oddly-cropped 1080p release still counts as FullHD, and an unknown height (0)
/// maps to <see cref="Quality.Any"/> so it never spuriously flags a cutoff.
/// </summary>
public static class QualityHelper
{
    /// <summary>Snap a raw pixel height to the highest quality tier it meets or exceeds. 0/unknown ⇒ Any.</summary>
    public static Quality FromHeight(int height) => height switch
    {
        >= 4320 => Quality.UHD8K,
        >= 2160 => Quality.UHD4K,
        >= 1080 => Quality.FullHD,
        >= 720 => Quality.HD,
        >= 480 => Quality.SD,
        _ => Quality.Any
    };

    /// <summary>Human label for a tier, e.g. "1080p" / "4K" / "Unknown".</summary>
    public static string Label(this Quality q) => q switch
    {
        Quality.SD => "480p",
        Quality.HD => "720p",
        Quality.FullHD => "1080p",
        Quality.UHD4K => "4K",
        Quality.UHD8K => "8K",
        _ => "Unknown"
    };
}
