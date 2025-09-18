using PlexRequestsHosted.Shared.Enums;
using System.ComponentModel.DataAnnotations;

namespace PlexRequestsHosted.Shared.DTOs;

public abstract class BaseDto
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class MediaCardDto : BaseDto
{
    [Required]
    public string Title { get; set; } = string.Empty;
    public string? Overview { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public int? Year { get; set; }
    public decimal? Rating { get; set; }
    public int? Runtime { get; set; }
    public MediaType MediaType { get; set; }
    public List<string> Genres { get; set; } = new();
    public string? Quality { get; set; }
    public bool IsAvailable { get; set; }
    public RequestStatus RequestStatus { get; set; }
    public string? PlexUrl { get; set; }
    public int TotalSeasons { get; set; }
    public int AvailableSeasons { get; set; }
    public string? ImdbId { get; set; }
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }
}

public class MediaDetailDto : MediaCardDto
{
    public string? Tagline { get; set; }
    public List<string> Cast { get; set; } = new();
    public List<string> Directors { get; set; } = new();
    public List<string> Writers { get; set; } = new();
    public string? Studio { get; set; }
    public string? Network { get; set; }
    public string? ContentRating { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public DateTime? FirstAired { get; set; }
    public DateTime? LastAired { get; set; }
    public string? Status { get; set; }
    public List<SeasonDto> Seasons { get; set; } = new();
    public string? TrailerUrl { get; set; }
    public List<string> Languages { get; set; } = new();
    public List<string> Countries { get; set; } = new();
}

public class SeasonDto
{
    public int SeasonNumber { get; set; }
    public string? Name { get; set; }
    public int EpisodeCount { get; set; }
    public string? PosterUrl { get; set; }
    public DateTime? AirDate { get; set; }
    public bool IsAvailable { get; set; }
    public int AvailableEpisodes { get; set; }
}

public class MediaRequestDto : BaseDto
{
    [Required]
    public int MediaId { get; set; }
    [Required]
    public MediaType MediaType { get; set; }
    [Required]
    public string Title { get; set; } = string.Empty;
    public string? PosterUrl { get; set; }
    public RequestStatus Status { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? AvailableAt { get; set; }
    public int RequestedByUserId { get; set; }
    public string RequestedByUsername { get; set; } = string.Empty;
    public int? ApprovedByUserId { get; set; }
    public string? ApprovedByUsername { get; set; }
    public Quality PreferredQuality { get; set; }
    public string? RequestNote { get; set; }
    public string? DenialReason { get; set; }
    public List<int> RequestedSeasons { get; set; } = new();
    public bool RequestAllSeasons { get; set; }
}

public class UserDto : BaseDto
{
    [Required]
    public string Username { get; set; } = string.Empty;
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public List<string> Roles { get; set; } = new();
    public UserPreferencesDto Preferences { get; set; } = new();
    public UserStatsDto Stats { get; set; } = new();
    public string? PlexUsername { get; set; }
    public string? PlexId { get; set; }
    public bool PlexLinked { get; set; }
    public int? MovieRequestLimit { get; set; }
    public int? TvRequestLimit { get; set; }
    public int? MusicRequestLimit { get; set; }
}

public class UserPreferencesDto
{
    public bool DarkMode { get; set; } = true;
    public string Language { get; set; } = "en";
    public bool EmailNotifications { get; set; } = true;
    public bool PushNotifications { get; set; } = false;
    public Quality DefaultQuality { get; set; } = Quality.FullHD;
    public bool AutoPlayTrailers { get; set; } = false;
    public bool ShowAdultContent { get; set; } = true;
    public SortOrder DefaultSort { get; set; } = SortOrder.DateDescending;
}

public class UserStatsDto
{
    public int TotalRequests { get; set; }
    public int ApprovedRequests { get; set; }
    public int PendingRequests { get; set; }
    public int AvailableRequests { get; set; }
    public DateTime? LastRequestDate { get; set; }
    public int MovieRequests { get; set; }
    public int TvRequests { get; set; }
    public int MusicRequests { get; set; }
}

public class PlexAuthRequest
{
    [Required]
    public string PlexToken { get; set; } = string.Empty;
    [Required]
    public string PlexUsername { get; set; } = string.Empty;
}

public class AuthResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public UserDto User { get; set; } = new();
}

public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasPrevious => PageNumber > 1;
    public bool HasNext => PageNumber < TotalPages;
}

public class MediaFilterDto
{
    public string? SearchTerm { get; set; }
    public MediaType? MediaType { get; set; }
    public List<string> Genres { get; set; } = new();
    public int? YearFrom { get; set; }
    public int? YearTo { get; set; }
    public decimal? MinRating { get; set; }
    public Quality? Quality { get; set; }
    public bool? OnlyAvailable { get; set; }
    public bool? OnlyRequested { get; set; }
    public SortOrder SortBy { get; set; } = SortOrder.DateDescending;
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class ApiResponse<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string? Message { get; set; }
    public List<string> Errors { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class MediaRequestResult
{
    public bool Success { get; set; }
    public int? RequestId { get; set; }
    public string? ErrorMessage { get; set; }
    public RequestStatus? NewStatus { get; set; }
}
