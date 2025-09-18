using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

public class SeedMetadataProvider : IMediaMetadataProvider
{
    private static readonly List<MediaCardDto> _seed = new()
    {
        new MediaCardDto{ Id=1, Title="Inception", Overview="A thief who steals corporate secrets through dream-sharing technology is given the inverse task of planting an idea.", PosterUrl="https://image.tmdb.org/t/p/w342/qmDpIHrmpJINaRKAfWQfftjCdyi.jpg", BackdropUrl="https://image.tmdb.org/t/p/w1280/s3TBrRGB1iav7gFOCNx3H31MoES.jpg", Year=2010, Rating=8.4m, Runtime=148, MediaType=MediaType.Movie, Genres=new(){"Action","Sci-Fi"}, Quality="4K", IsAvailable=true, RequestStatus=RequestStatus.Available, PlexUrl="https://app.plex.tv/" },
        new MediaCardDto{ Id=2, Title="The Matrix", Overview="A computer hacker learns about the true nature of his reality.", PosterUrl="https://image.tmdb.org/t/p/w342/f89U3ADr1oiB1s9GkdPOEpXUk5H.jpg", BackdropUrl="https://image.tmdb.org/t/p/w1280/7u3pxc0K1wx32IleAkLv78MKgrw.jpg", Year=1999, Rating=8.7m, Runtime=136, MediaType=MediaType.Movie, Genres=new(){"Action","Sci-Fi"}, Quality="1080p", IsAvailable=false, RequestStatus=RequestStatus.None},
        new MediaCardDto{ Id=3, Title="Breaking Bad", Overview="A chemistry teacher turned methamphetamine producer.", PosterUrl="https://image.tmdb.org/t/p/w342/ggFHVNu6YYI5L9pCfOacjizRGt.jpg", BackdropUrl="https://image.tmdb.org/t/p/w1280/tsRy63Mu5cu8etL1X7ZLyf7UP1M.jpg", Year=2008, Rating=9.5m, MediaType=MediaType.TvShow, Genres=new(){"Crime","Drama"}, IsAvailable=true, RequestStatus=RequestStatus.Available, TotalSeasons=5, AvailableSeasons=5 }
    };

    public Task<List<MediaCardDto>> SearchAsync(string query, MediaType? mediaType = null, int page = 1, int pageSize = 20)
        => Task.FromResult(_seed.Where(x => (mediaType == null || x.MediaType == mediaType) && (string.IsNullOrWhiteSpace(query) || x.Title.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList());

    public Task<MediaDetailDto?> GetDetailsAsync(int mediaId, MediaType mediaType)
    {
        var card = _seed.FirstOrDefault(x => x.Id == mediaId && x.MediaType == mediaType);
        if (card == null) return Task.FromResult<MediaDetailDto?>(null);
        var detail = new MediaDetailDto
        {
            Id = card.Id,
            Title = card.Title,
            Overview = card.Overview,
            PosterUrl = card.PosterUrl,
            BackdropUrl = card.BackdropUrl,
            MediaType = card.MediaType,
            Rating = card.Rating,
            Tagline = card.MediaType == MediaType.Movie ? "Your mind is the scene of the crime." : "",
            IsAvailable = card.IsAvailable,
            PlexUrl = card.PlexUrl,
            TotalSeasons = card.TotalSeasons,
            AvailableSeasons = card.AvailableSeasons
        };
        return Task.FromResult<MediaDetailDto?>(detail);
    }

    public Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10)
        => Task.FromResult(_seed.Take(count).ToList());

    public Task<List<MediaCardDto>> GetLibraryAsync(MediaType mediaType, int page = 1, int pageSize = 20)
        => Task.FromResult(_seed.Where(x => x.MediaType == mediaType).ToList());
}
