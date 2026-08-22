using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared.Navigation;

/// <summary>Parses the stable, human-readable media type tokens used by search deep links.</summary>
public static class SearchNavigationContext
{
    public static MediaType? ParseMediaType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "movie" or "movies" => MediaType.Movie,
        "tv" or "tvshow" or "tvshows" or "tv show" or "tv shows" or "show" or "shows" or "series" => MediaType.TvShow,
        "anime" => MediaType.Anime,
        "music" => MediaType.Music,
        _ => null
    };

    /// <summary>Builds one canonical, refresh-safe search URL for every media module.</summary>
    public static string BuildUrl(
        string? query = null,
        MediaType? mediaType = null,
        MediaKind? musicKind = null,
        string? source = null)
    {
        var parameters = new List<string>();
        if (mediaType.HasValue)
            parameters.Add($"type={ToRouteToken(mediaType.Value)}");

        if (mediaType == MediaType.Music)
        {
            var kind = musicKind is MediaKind.Album or MediaKind.Artist or MediaKind.Track
                ? musicKind.Value
                : MediaKind.Album;
            parameters.Add($"kind={kind.ToString().ToLowerInvariant()}");
            if (!string.IsNullOrWhiteSpace(source))
                parameters.Add($"source={Uri.EscapeDataString(source.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(query))
            parameters.Add($"q={Uri.EscapeDataString(query.Trim())}");

        return parameters.Count == 0 ? "/search" : $"/search?{string.Join('&', parameters)}";
    }

    private static string ToRouteToken(MediaType mediaType) => mediaType switch
    {
        MediaType.Movie => "movie",
        MediaType.TvShow => "tvshow",
        MediaType.Anime => "anime",
        MediaType.Music => "music",
        _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, null)
    };
}
