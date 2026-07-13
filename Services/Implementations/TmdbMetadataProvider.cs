using Microsoft.Extensions.Caching.Memory;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using TMDbLib.Client;
using TMDbLib.Objects.General;
using TMDbLib.Objects.Movies;
using TMDbLib.Objects.Search;
using TMDbLib.Objects.TvShows;
using TMDbLib.Objects.Authentication;
using TMDbLib.Objects.Discover;
using TMDbLib.Objects.Trending;

namespace PlexRequestsHosted.Services.Implementations;

public class TmdbMetadataProvider : IMediaMetadataProvider
{
    private readonly TMDbClient _client;
    private readonly ILogger<TmdbMetadataProvider> _logger;
    // Shared IMemoryCache (singleton) with real TTLs — replaces the old never-expiring static
    // dictionaries. Entries expire so trending/popular stay fresh and memory can't grow unbounded.
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _mem;
    private static readonly TimeSpan DiscoverTtl = TimeSpan.FromHours(1);   // trending/popular/library/genre
    private static readonly TimeSpan SearchTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DetailTtl = TimeSpan.FromHours(12);

    public TmdbMetadataProvider(IConfiguration configuration, ILogger<TmdbMetadataProvider> logger, Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
    {
        _logger = logger;
        _mem = cache;
        var apiKey = configuration["ApiKeys:TMDb:ApiKey"];
        var accessToken = configuration["ApiKeys:TMDb:ReadAccessToken"];

        if (string.IsNullOrEmpty(apiKey) && string.IsNullOrEmpty(accessToken))
        {
            throw new InvalidOperationException("TMDB API key or access token must be configured");
        }

        _client = new TMDbClient(apiKey ?? accessToken!);
    }

    public string ProviderKey => "tmdb";
    public bool RequiresApiKey => true;
    public bool IsAvailable => true;   // only constructed when a key/token is present
    public bool Supports(PlexRequestsHosted.Shared.Enums.MediaType mediaType)
        => mediaType is PlexRequestsHosted.Shared.Enums.MediaType.Movie or PlexRequestsHosted.Shared.Enums.MediaType.TvShow or PlexRequestsHosted.Shared.Enums.MediaType.Anime;

    private T CacheGetOrNull<T>(string key) where T : class => _mem.TryGetValue(key, out var v) ? v as T : null;
    private T CacheSet<T>(string key, T value, TimeSpan ttl) { _mem.Set(key, value, ttl); return value; }

    public async Task<List<MediaCardDto>> SearchAsync(string query, PlexRequestsHosted.Shared.Enums.MediaType? mediaType = null, int page = 1, int pageSize = 20)
    {
        try
        {
            var cacheKey = $"search_{query}_{mediaType}_{page}_{pageSize}";
            if (CacheGetOrNull<List<MediaCardDto>>(cacheKey) is { } cached) return cached;

            var results = new List<MediaCardDto>();

            if (mediaType == null || mediaType == PlexRequestsHosted.Shared.Enums.MediaType.Movie)
            {
                var movieResults = await _client.SearchMovieAsync(query, page: page, includeAdult: true);
                results.AddRange(movieResults.Results.Select(MapMovieToCard));
            }

            if (mediaType == null || mediaType == PlexRequestsHosted.Shared.Enums.MediaType.TvShow)
            {
                var tvResults = await _client.SearchTvShowAsync(query, page: page, includeAdult: true);
                results.AddRange(tvResults.Results.Select(MapTvShowToCard));
            }

            // Apply page size limit
            results = results.Take(pageSize).ToList();

            return CacheSet(cacheKey, results, SearchTtl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching TMDB for query: {Query}", query);
            return new List<MediaCardDto>();
        }
    }

    public async Task<MediaDetailDto?> GetDetailsAsync(int mediaId, PlexRequestsHosted.Shared.Enums.MediaType mediaType)
    {
        try
        {
            var cacheKey = $"detail_{mediaType}_{mediaId}";
            if (CacheGetOrNull<MediaDetailDto>(cacheKey) is { } cached) return cached;

            MediaDetailDto? result = null;

            if (mediaType == PlexRequestsHosted.Shared.Enums.MediaType.Movie)
            {
                var movie = await _client.GetMovieAsync(mediaId, MovieMethods.Credits | MovieMethods.Videos | MovieMethods.Images | MovieMethods.ExternalIds);
                if (movie != null)
                {
                    result = MapMovieToDetail(movie);
                }
            }
            else if (mediaType == PlexRequestsHosted.Shared.Enums.MediaType.TvShow)
            {
                var tvShow = await _client.GetTvShowAsync(mediaId, TvShowMethods.Credits | TvShowMethods.Videos | TvShowMethods.Images | TvShowMethods.ExternalIds);
                if (tvShow != null)
                {
                    result = MapTvShowToDetail(tvShow);
                }
            }

            if (result != null) CacheSet(cacheKey, result, DetailTtl);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting TMDB details for {MediaType} {MediaId}", mediaType, mediaId);
            return null;
        }
    }

    public async Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10)
    {
        try
        {
            var cacheKey = $"recent_{count}";
            if (CacheGetOrNull<List<MediaCardDto>>(cacheKey) is { } cached) return cached;

            // Use discover endpoints for popular content
            var movies = await _client.DiscoverMoviesAsync().Query();
            var tvShows = await _client.DiscoverTvShowsAsync().Query();

            var results = new List<MediaCardDto>();
            results.AddRange(movies.Results.Take(count / 2).Select(m => new MediaCardDto
            {
                Id = m.Id,
                Title = m.Title ?? "Unknown",
                Overview = m.Overview,
                PosterUrl = m.PosterPath != null ? $"https://image.tmdb.org/t/p/w342{m.PosterPath}" : null,
                BackdropUrl = m.BackdropPath != null ? $"https://image.tmdb.org/t/p/w1280{m.BackdropPath}" : null,
                Year = m.ReleaseDate?.Year,
                Rating = (decimal?)m.VoteAverage,
                MediaType = PlexRequestsHosted.Shared.Enums.MediaType.Movie,
                Genres = m.GenreIds?.Select(id => GetGenreName(id)).ToList() ?? new List<string>(),
                TmdbId = m.Id
            }));

            results.AddRange(tvShows.Results.Take(count / 2).Select(t => new MediaCardDto
            {
                Id = t.Id,
                Title = t.Name ?? "Unknown",
                Overview = t.Overview,
                PosterUrl = t.PosterPath != null ? $"https://image.tmdb.org/t/p/w342{t.PosterPath}" : null,
                BackdropUrl = t.BackdropPath != null ? $"https://image.tmdb.org/t/p/w1280{t.BackdropPath}" : null,
                Year = t.FirstAirDate?.Year,
                Rating = (decimal?)t.VoteAverage,
                MediaType = PlexRequestsHosted.Shared.Enums.MediaType.TvShow,
                Genres = t.GenreIds?.Select(id => GetGenreName(id)).ToList() ?? new List<string>(),
                TmdbId = t.Id
            }));

            return CacheSet(cacheKey, results, DiscoverTtl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recently added from TMDB");
            return new List<MediaCardDto>();
        }
    }

    public async Task<List<MediaCardDto>> GetLibraryAsync(PlexRequestsHosted.Shared.Enums.MediaType mediaType, int page = 1, int pageSize = 20)
    {
        try
        {
            var cacheKey = $"library_{mediaType}_{page}_{pageSize}";
            if (CacheGetOrNull<List<MediaCardDto>>(cacheKey) is { } cached) return cached;

            var results = new List<MediaCardDto>();

            if (mediaType == PlexRequestsHosted.Shared.Enums.MediaType.Movie)
            {
                var movies = await _client.DiscoverMoviesAsync().Query(page: page);
                results.AddRange(movies.Results.Take(pageSize).Select(MapMovieToCard));
            }
            else if (mediaType == PlexRequestsHosted.Shared.Enums.MediaType.TvShow)
            {
                var tvShows = await _client.DiscoverTvShowsAsync().Query(page: page);
                results.AddRange(tvShows.Results.Take(pageSize).Select(MapTvShowToCard));
            }

            return CacheSet(cacheKey, results, DiscoverTtl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting TMDB library for {MediaType}", mediaType);
            return new List<MediaCardDto>();
        }
    }

    // ---- Discovery (real TMDB endpoints) ----

    private async Task<List<MediaCardDto>> CachedAsync(string cacheKey, Func<Task<List<MediaCardDto>>> factory)
    {
        if (CacheGetOrNull<List<MediaCardDto>>(cacheKey) is { } cached) return cached;
        try
        {
            return CacheSet(cacheKey, await factory(), DiscoverTtl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TMDB discovery failed for {Key}", cacheKey);
            return new List<MediaCardDto>();
        }
    }

    // Interleave movie + TV results so a mixed feed alternates types instead of grouping them.
    private static List<MediaCardDto> Interleave(List<MediaCardDto> a, List<MediaCardDto> b, int take)
    {
        var merged = new List<MediaCardDto>(a.Count + b.Count);
        int i = 0, j = 0;
        while (i < a.Count || j < b.Count)
        {
            if (i < a.Count) merged.Add(a[i++]);
            if (j < b.Count) merged.Add(b[j++]);
        }
        return merged.Take(take).ToList();
    }

    public Task<List<MediaCardDto>> GetTrendingAsync(PlexRequestsHosted.Shared.Enums.MediaType? mediaType = null, int page = 1, int pageSize = 20)
        => CachedAsync($"trending_{mediaType}_{page}_{pageSize}", async () =>
        {
            var movies = new List<MediaCardDto>();
            var tv = new List<MediaCardDto>();
            if (mediaType is null or PlexRequestsHosted.Shared.Enums.MediaType.Movie)
            {
                var m = await _client.GetTrendingMoviesAsync(TimeWindow.Week, page);
                movies.AddRange(m.Results.Select(MapMovieToCard));
            }
            if (mediaType is null or PlexRequestsHosted.Shared.Enums.MediaType.TvShow)
            {
                var t = await _client.GetTrendingTvAsync(TimeWindow.Week, page);
                tv.AddRange(t.Results.Select(MapTvShowToCard));
            }
            return mediaType == null ? Interleave(movies, tv, pageSize) : movies.Concat(tv).Take(pageSize).ToList();
        });

    public Task<List<MediaCardDto>> GetPopularAsync(PlexRequestsHosted.Shared.Enums.MediaType mediaType, int page = 1, int pageSize = 20)
        => CachedAsync($"popular_{mediaType}_{page}_{pageSize}", async () =>
        {
            if (mediaType == PlexRequestsHosted.Shared.Enums.MediaType.Movie)
            {
                var m = await _client.GetMoviePopularListAsync(page: page);
                return m.Results.Take(pageSize).Select(MapMovieToCard).ToList();
            }
            var t = await _client.GetTvShowPopularAsync(page: page);
            return t.Results.Take(pageSize).Select(MapTvShowToCard).ToList();
        });

    public Task<List<MediaCardDto>> GetTopRatedAsync(PlexRequestsHosted.Shared.Enums.MediaType mediaType, int page = 1, int pageSize = 20)
        => CachedAsync($"toprated_{mediaType}_{page}_{pageSize}", async () =>
        {
            if (mediaType == PlexRequestsHosted.Shared.Enums.MediaType.Movie)
            {
                var m = await _client.GetMovieTopRatedListAsync(page: page);
                return m.Results.Take(pageSize).Select(MapMovieToCard).ToList();
            }
            var t = await _client.GetTvShowTopRatedAsync(page: page);
            return t.Results.Take(pageSize).Select(MapTvShowToCard).ToList();
        });

    public Task<List<MediaCardDto>> GetByGenreAsync(PlexRequestsHosted.Shared.Enums.MediaType mediaType, string genre, int page = 1, int pageSize = 20)
        => CachedAsync($"genre_{mediaType}_{genre}_{page}_{pageSize}", async () =>
        {
            var genreId = GetGenreId(genre);
            if (genreId == 0) return new List<MediaCardDto>();
            if (mediaType == PlexRequestsHosted.Shared.Enums.MediaType.Movie)
            {
                var m = await _client.DiscoverMoviesAsync()
                    .IncludeWithAllOfGenre(new[] { genreId })
                    .OrderBy(DiscoverMovieSortBy.PopularityDesc)
                    .Query(page);
                return m.Results.Take(pageSize).Select(MapMovieToCard).ToList();
            }
            var t = await _client.DiscoverTvShowsAsync()
                .WhereGenresInclude(new[] { genreId })
                .OrderBy(DiscoverTvShowSortBy.PopularityDesc)
                .Query(page);
            return t.Results.Take(pageSize).Select(MapTvShowToCard).ToList();
        });

    public Task<List<MediaCardDto>> GetSimilarAsync(int mediaId, PlexRequestsHosted.Shared.Enums.MediaType mediaType, int count = 12)
        => CachedAsync($"similar_{mediaType}_{mediaId}_{count}", async () =>
        {
            if (mediaType == PlexRequestsHosted.Shared.Enums.MediaType.Movie)
            {
                var m = await _client.GetMovieRecommendationsAsync(mediaId);
                var list = m.Results.Select(MapMovieToCard).ToList();
                if (list.Count == 0)
                {
                    var s = await _client.GetMovieSimilarAsync(mediaId);
                    list = s.Results.Select(MapMovieToCard).ToList();
                }
                return list.Take(count).ToList();
            }
            var tv = await _client.GetTvShowRecommendationsAsync(mediaId);
            var tvList = tv.Results.Select(MapTvShowToCard).ToList();
            if (tvList.Count == 0)
            {
                var s = await _client.GetTvShowSimilarAsync(mediaId);
                tvList = s.Results.Select(MapTvShowToCard).ToList();
            }
            return tvList.Take(count).ToList();
        });

    public async Task<List<EpisodeDto>> GetSeasonEpisodesAsync(int showId, int seasonNumber)
    {
        var key = $"episodes_{showId}_{seasonNumber}";
        if (CacheGetOrNull<List<EpisodeDto>>(key) is { } cached) return cached;
        try
        {
            var season = await _client.GetTvSeasonAsync(showId, seasonNumber);
            var list = (season?.Episodes ?? new()).Select(e => new EpisodeDto
            {
                SeasonNumber = seasonNumber,
                EpisodeNumber = e.EpisodeNumber,
                Name = e.Name,
                Overview = e.Overview,
                StillUrl = e.StillPath != null ? $"https://image.tmdb.org/t/p/w300{e.StillPath}" : null,
                AirDate = e.AirDate
            }).ToList();
            return CacheSet(key, list, DetailTtl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TMDB episodes failed for show {Show} S{Season}", showId, seasonNumber);
            return new List<EpisodeDto>();
        }
    }

    private static MediaCardDto MapMovieToCard(SearchMovie movie)
    {
        return new MediaCardDto
        {
            Id = movie.Id,
            Title = movie.Title ?? "Unknown",
            Overview = movie.Overview,
            PosterUrl = movie.PosterPath != null ? $"https://image.tmdb.org/t/p/w342{movie.PosterPath}" : null,
            BackdropUrl = movie.BackdropPath != null ? $"https://image.tmdb.org/t/p/w1280{movie.BackdropPath}" : null,
            Year = movie.ReleaseDate?.Year,
            Rating = (decimal?)movie.VoteAverage,
            MediaType = PlexRequestsHosted.Shared.Enums.MediaType.Movie,
            Genres = movie.GenreIds?.Select(id => GetGenreName(id)).ToList() ?? new List<string>(),
            TmdbId = movie.Id
        };
    }

    private static MediaCardDto MapTvShowToCard(SearchTv tvShow)
    {
        return new MediaCardDto
        {
            Id = tvShow.Id,
            Title = tvShow.Name ?? "Unknown",
            Overview = tvShow.Overview,
            PosterUrl = tvShow.PosterPath != null ? $"https://image.tmdb.org/t/p/w342{tvShow.PosterPath}" : null,
            BackdropUrl = tvShow.BackdropPath != null ? $"https://image.tmdb.org/t/p/w1280{tvShow.BackdropPath}" : null,
            Year = tvShow.FirstAirDate?.Year,
            Rating = (decimal?)tvShow.VoteAverage,
            MediaType = PlexRequestsHosted.Shared.Enums.MediaType.TvShow,
            Genres = tvShow.GenreIds?.Select(id => GetGenreName(id)).ToList() ?? new List<string>(),
            TmdbId = tvShow.Id
        };
    }

    private static MediaDetailDto MapMovieToDetail(Movie movie)
    {
        return new MediaDetailDto
        {
            Id = movie.Id,
            Title = movie.Title ?? "Unknown",
            Overview = movie.Overview,
            PosterUrl = movie.PosterPath != null ? $"https://image.tmdb.org/t/p/w342{movie.PosterPath}" : null,
            BackdropUrl = movie.BackdropPath != null ? $"https://image.tmdb.org/t/p/w1280{movie.BackdropPath}" : null,
            Year = movie.ReleaseDate?.Year,
            Rating = (decimal?)movie.VoteAverage,
            Runtime = movie.Runtime,
            MediaType = PlexRequestsHosted.Shared.Enums.MediaType.Movie,
            Genres = movie.Genres?.Select(g => g.Name).ToList() ?? new List<string>(),
            Cast = movie.Credits?.Cast?.Take(10).Select(c => c.Name).ToList() ?? new List<string>(),
            Directors = movie.Credits?.Crew?.Where(c => c.Job == "Director").Select(c => c.Name).ToList() ?? new List<string>(),
            Writers = movie.Credits?.Crew?.Where(c => c.Job == "Writer" || c.Job == "Screenplay").Select(c => c.Name).ToList() ?? new List<string>(),
            Tagline = movie.Tagline,
            Studio = movie.ProductionCompanies?.FirstOrDefault()?.Name,
            ReleaseDate = movie.ReleaseDate,
            Languages = new List<string>(),
            Countries = new List<string>(),
            TmdbId = movie.Id,
            ImdbId = movie.ExternalIds?.ImdbId ?? movie.ImdbId
        };
    }

    private static MediaDetailDto MapTvShowToDetail(TvShow tvShow)
    {
        return new MediaDetailDto
        {
            Id = tvShow.Id,
            Title = tvShow.Name ?? "Unknown",
            Overview = tvShow.Overview,
            PosterUrl = tvShow.PosterPath != null ? $"https://image.tmdb.org/t/p/w342{tvShow.PosterPath}" : null,
            BackdropUrl = tvShow.BackdropPath != null ? $"https://image.tmdb.org/t/p/w1280{tvShow.BackdropPath}" : null,
            Year = tvShow.FirstAirDate?.Year,
            Rating = (decimal?)tvShow.VoteAverage,
            MediaType = PlexRequestsHosted.Shared.Enums.MediaType.TvShow,
            Genres = tvShow.Genres?.Select(g => g.Name).ToList() ?? new List<string>(),
            Cast = tvShow.Credits?.Cast?.Take(10).Select(c => c.Name).ToList() ?? new List<string>(),
            Directors = tvShow.CreatedBy?.Select(c => c.Name).ToList() ?? new List<string>(),
            Tagline = null,
            Network = tvShow.Networks?.FirstOrDefault()?.Name,
            FirstAired = tvShow.FirstAirDate,
            LastAired = tvShow.LastAirDate,
            Status = tvShow.Status,
            TotalSeasons = tvShow.NumberOfSeasons,
            Languages = new List<string>(),
            Countries = new List<string>(),
            Seasons = tvShow.Seasons?.Select(s => new SeasonDto
            {
                SeasonNumber = s.SeasonNumber,
                Name = s.Name,
                EpisodeCount = s.EpisodeCount,
                PosterUrl = s.PosterPath != null ? $"https://image.tmdb.org/t/p/w342{s.PosterPath}" : null,
                AirDate = s.AirDate,
                IsAvailable = false
            }).ToList() ?? new List<SeasonDto>(),
            TmdbId = tvShow.Id,
            ImdbId = tvShow.ExternalIds?.ImdbId
        };
    }

    public async Task<string?> GetImdbIdAsync(int mediaId, PlexRequestsHosted.Shared.Enums.MediaType mediaType)
    {
        try
        {
            if (mediaType == PlexRequestsHosted.Shared.Enums.MediaType.Movie)
            {
                var movie = await _client.GetMovieAsync(mediaId, MovieMethods.ExternalIds);
                return movie?.ExternalIds?.ImdbId;
            }
            if (mediaType == PlexRequestsHosted.Shared.Enums.MediaType.TvShow)
            {
                var tv = await _client.GetTvShowAsync(mediaId, TvShowMethods.ExternalIds);
                return tv?.ExternalIds?.ImdbId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve IMDb id for {MediaType} {MediaId}", mediaType, mediaId);
        }
        return null;
    }

    private static string GetGenreName(int genreId)
    {
        return genreId switch
        {
            28 => "Action",
            12 => "Adventure",
            16 => "Animation",
            35 => "Comedy",
            80 => "Crime",
            99 => "Documentary",
            18 => "Drama",
            10751 => "Family",
            14 => "Fantasy",
            36 => "History",
            27 => "Horror",
            10402 => "Music",
            9648 => "Mystery",
            10749 => "Romance",
            878 => "Science Fiction",
            10770 => "TV Movie",
            53 => "Thriller",
            10752 => "War",
            37 => "Western",
            // TV Show genres
            10759 => "Action & Adventure",
            10762 => "Kids",
            10763 => "News",
            10764 => "Reality",
            10765 => "Sci-Fi & Fantasy",
            10766 => "Soap",
            10767 => "Talk",
            10768 => "War & Politics",
            _ => "Unknown"
        };
    }

    // Reverse of GetGenreName: map a genre display name to its TMDB id (0 = unknown). Accepts both the
    // movie and TV spellings so a single "Action" row works across types.
    private static int GetGenreId(string genre) => genre?.Trim().ToLowerInvariant() switch
    {
        "action" => 28,
        "adventure" => 12,
        "animation" => 16,
        "comedy" => 35,
        "crime" => 80,
        "documentary" => 99,
        "drama" => 18,
        "family" => 10751,
        "fantasy" => 14,
        "history" => 36,
        "horror" => 27,
        "music" => 10402,
        "mystery" => 9648,
        "romance" => 10749,
        "science fiction" or "sci-fi" => 878,
        "thriller" => 53,
        "war" => 10752,
        "western" => 37,
        "action & adventure" => 10759,
        "kids" => 10762,
        "reality" => 10764,
        "sci-fi & fantasy" => 10765,
        "war & politics" => 10768,
        _ => 0
    };
}
