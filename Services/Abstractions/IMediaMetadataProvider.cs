using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Abstractions;

public interface IMediaMetadataProvider
{
    Task<List<MediaCardDto>> SearchAsync(string query, MediaType? mediaType = null, int page = 1, int pageSize = 20);
    Task<MediaDetailDto?> GetDetailsAsync(int mediaId, MediaType mediaType);
    Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10);
    Task<List<MediaCardDto>> GetLibraryAsync(MediaType mediaType, int page = 1, int pageSize = 20);

    /// <summary>Resolve the IMDb id (e.g. "tt0137523") for a media item, or null if unavailable.</summary>
    Task<string?> GetImdbIdAsync(int mediaId, MediaType mediaType);

    // ---- Discovery ----
    // Default implementations fall back to the library/search feed so providers that don't have real
    // discovery endpoints (Seed, Trakt) keep working. The TMDB provider overrides these with the
    // dedicated trending/popular/top-rated/genre/recommendation endpoints.

    /// <summary>Trending titles (mixed types when <paramref name="mediaType"/> is null).</summary>
    Task<List<MediaCardDto>> GetTrendingAsync(MediaType? mediaType = null, int page = 1, int pageSize = 20)
        => GetLibraryAsync(mediaType ?? MediaType.Movie, page, pageSize);

    /// <summary>Most popular titles of one type.</summary>
    Task<List<MediaCardDto>> GetPopularAsync(MediaType mediaType, int page = 1, int pageSize = 20)
        => GetLibraryAsync(mediaType, page, pageSize);

    /// <summary>Highest-rated titles of one type.</summary>
    Task<List<MediaCardDto>> GetTopRatedAsync(MediaType mediaType, int page = 1, int pageSize = 20)
        => GetLibraryAsync(mediaType, page, pageSize);

    /// <summary>Titles of one type filtered by a genre name (e.g. "Action").</summary>
    Task<List<MediaCardDto>> GetByGenreAsync(MediaType mediaType, string genre, int page = 1, int pageSize = 20)
        => GetLibraryAsync(mediaType, page, pageSize);

    /// <summary>Titles similar to / recommended from a given title.</summary>
    Task<List<MediaCardDto>> GetSimilarAsync(int mediaId, MediaType mediaType, int count = 12)
        => GetLibraryAsync(mediaType, 1, count);

    /// <summary>Episodes of one season (with air dates), for episode-level requests + monitoring.</summary>
    Task<List<EpisodeDto>> GetSeasonEpisodesAsync(int showId, int seasonNumber)
        => Task.FromResult(new List<EpisodeDto>());
}
