using System.Collections.Concurrent;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using TMDbLib.Client;
using TMDbLib.Objects.General;
using TMDbLib.Objects.Movies;
using TMDbLib.Objects.Search;
using TMDbLib.Objects.TvShows;
using TMDbLib.Objects.Authentication;

namespace PlexRequestsHosted.Services.Implementations;

public class TmdbMetadataProvider : IMediaMetadataProvider
{
    private readonly TMDbClient _client;
    private readonly ILogger<TmdbMetadataProvider> _logger;
    private static readonly ConcurrentDictionary<string, List<MediaCardDto>> _cache = new();
    private static readonly ConcurrentDictionary<int, MediaDetailDto> _detailCache = new();

    public TmdbMetadataProvider(IConfiguration configuration, ILogger<TmdbMetadataProvider> logger)
    {
        _logger = logger;
        var apiKey = configuration["ApiKeys:TMDb:ApiKey"];
        var accessToken = configuration["ApiKeys:TMDb:ReadAccessToken"];

        if (string.IsNullOrEmpty(apiKey) && string.IsNullOrEmpty(accessToken))
        {
            throw new InvalidOperationException("TMDB API key or access token must be configured");
        }

        _client = new TMDbClient(apiKey ?? accessToken!);
    }

    public async Task<List<MediaCardDto>> SearchAsync(string query, PlexRequestsHosted.Shared.Enums.MediaType? mediaType = null, int page = 1, int pageSize = 20)
    {
        try
        {
            var cacheKey = $"search_{query}_{mediaType}_{page}_{pageSize}";
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

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

            _cache.TryAdd(cacheKey, results);
            return results;
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
            var cacheKey = $"{mediaType}_{mediaId}";
            if (_detailCache.TryGetValue(cacheKey.GetHashCode(), out var cached))
            {
                return cached;
            }

            MediaDetailDto? result = null;

            if (mediaType == PlexRequestsHosted.Shared.Enums.MediaType.Movie)
            {
                var movie = await _client.GetMovieAsync(mediaId, MovieMethods.Credits | MovieMethods.Videos | MovieMethods.Images);
                if (movie != null)
                {
                    result = MapMovieToDetail(movie);
                }
            }
            else if (mediaType == PlexRequestsHosted.Shared.Enums.MediaType.TvShow)
            {
                var tvShow = await _client.GetTvShowAsync(mediaId, TvShowMethods.Credits | TvShowMethods.Videos | TvShowMethods.Images);
                if (tvShow != null)
                {
                    result = MapTvShowToDetail(tvShow);
                }
            }

            if (result != null)
            {
                _detailCache.TryAdd(cacheKey.GetHashCode(), result);
            }

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
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

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

            _cache.TryAdd(cacheKey, results);
            return results;
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
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

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

            _cache.TryAdd(cacheKey, results);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting TMDB library for {MediaType}", mediaType);
            return new List<MediaCardDto>();
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
            TmdbId = movie.Id
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
            TmdbId = tvShow.Id
        };
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
}
