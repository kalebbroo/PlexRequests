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
}
