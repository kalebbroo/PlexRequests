using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Services.Abstractions;

public interface IMediaMetadataProvider
{
    // ---- Provider identity & capabilities (drive the modular multi-provider router) ----
    // Defaults keep existing providers working; each concrete provider overrides as needed.

    /// <summary>Stable key used in config to select this provider, e.g. "tmdb", "tvdb", "musicbrainz", "seed".</summary>
    string ProviderKey => "unknown";

    /// <summary>True when the provider needs an API key/token to function (affects keyless-fallback selection).</summary>
    bool RequiresApiKey => false;

    /// <summary>True when the provider can actually serve requests right now (its key/config is present).</summary>
    bool IsAvailable => true;

    /// <summary>Which media types this provider can serve. Default: movies/TV/anime (the TMDb-style set).</summary>
    bool Supports(MediaType mediaType) => mediaType is MediaType.Movie or MediaType.TvShow or MediaType.Anime;

    Task<List<MediaCardDto>> SearchAsync(string query, MediaType? mediaType = null, int page = 1, int pageSize = 20);
    Task<List<MediaCardDto>> SearchAsync(MediaSearchQuery query)
        => SearchAsync(query.Query, query.MediaType, query.Page, query.PageSize);
    Task<MediaDetailDto?> GetDetailsAsync(int mediaId, MediaType mediaType);
    /// <summary>Provider-neutral detail lookup. Legacy providers automatically accept TMDb identities.</summary>
    Task<MediaDetailDto?> GetDetailsAsync(MediaRef mediaRef) => mediaRef.TryGetTmdbId(out var tmdbId)
        ? GetDetailsAsync(tmdbId, mediaRef.MediaType)
        : Task.FromResult<MediaDetailDto?>(null);
    Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10);
    Task<List<MediaCardDto>> GetLibraryAsync(MediaType mediaType, int page = 1, int pageSize = 20);

    /// <summary>Resolve the IMDb id (e.g. "tt0137523") for a media item, or null if unavailable.</summary>
    Task<string?> GetImdbIdAsync(int mediaId, MediaType mediaType);
    Task<string?> GetImdbIdAsync(MediaRef mediaRef) => mediaRef.TryGetTmdbId(out var tmdbId)
        ? GetImdbIdAsync(tmdbId, mediaRef.MediaType)
        : Task.FromResult<string?>(null);

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

    Task<List<MediaCardDto>> GetSimilarAsync(MediaRef mediaRef, int count = 12) =>
        mediaRef.TryGetTmdbId(out var tmdbId)
            ? GetSimilarAsync(tmdbId, mediaRef.MediaType, count)
            : Task.FromResult(new List<MediaCardDto>());

    /// <summary>Episodes of one season (with air dates), for episode-level requests + monitoring.</summary>
    Task<List<EpisodeDto>> GetSeasonEpisodesAsync(int showId, int seasonNumber)
        => Task.FromResult(new List<EpisodeDto>());
}
