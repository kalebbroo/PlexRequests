using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Discovery;

/// <summary>
/// The discovery lists the home and browse pages show. Shared so the cache warm-up requests exactly the lists
/// the pages request: TMDB cache keys include the genre and page size, so a drift would warm nothing silently.
/// </summary>
public static class DiscoveryRows
{
    /// <summary>Titles fetched per discovery list. Rows render a window of these and reveal the rest in place.</summary>
    public const int RowSize = 100;

    private static readonly string[] MovieRowGenres = { "Action", "Comedy", "Drama", "Science Fiction", "Horror", "Animation", "Thriller" };
    private static readonly string[] TvRowGenres = { "Action & Adventure", "Comedy", "Drama", "Sci-Fi & Fantasy", "Animation", "Crime" };

    /// <summary>The curated genre rows shown on the browse page for a media type.</summary>
    public static string[] RowGenres(MediaType type) => type == MediaType.TvShow ? TvRowGenres : MovieRowGenres;
}
