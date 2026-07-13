using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Abstractions;

public interface IMediaRequestService
{
    Task<PagedResult<MediaRequestDto>> GetRequestsAsync(MediaFilterDto filter);
    Task<MediaRequestDto?> GetRequestByIdAsync(int id);
    Task<MediaRequestResult> RequestMediaAsync(int mediaId, MediaType mediaType);
    Task<MediaRequestResult> RequestSeasonsAsync(int mediaId, MediaType mediaType, List<int> seasons);
    Task<MediaRequestResult> RequestEpisodesAsync(int mediaId, MediaType mediaType, List<(int season, int episode)> episodes);
    Task<MediaRequestResult> RequestSeriesAsync(int mediaId, MediaType mediaType, bool monitor);
    Task<MediaRequestResult> CreateMonitoredEpisodeAsync(int anchorRequestId, int season, int episode);
    // User-scoped variants for callers without a cookie session (Discord bridge).
    Task<MediaRequestResult> RequestMediaForUserAsync(int userId, int mediaId, MediaType mediaType);
    Task<List<MediaRequestDto>> GetRequestsForUserAsync(int userId, int take = 25);
    Task<bool> CancelRequestAsync(int requestId);
    Task<bool> IsInWatchlistAsync(int mediaId, MediaType mediaType);
    Task<bool> AddToWatchlistAsync(int mediaId, MediaType mediaType);
    Task<bool> RemoveFromWatchlistAsync(int mediaId, MediaType mediaType);
    Task<List<MediaCardDto>> GetWatchlistAsync();
    Task<UserStatsDto> GetMyStatsAsync();
    Task<bool> CheckRequestLimitsAsync(MediaType mediaType);
    // Admin processing
    Task<bool> ApproveRequestAsync(int requestId, string? note = null);
    Task<bool> DenyRequestAsync(int requestId, string reason);
    Task<bool> MarkAvailableAsync(int requestId);
    // Admin processing without a cookie auth check (caller pre-verified admin — Discord bridge).
    Task<bool> ApproveRequestAsAdminAsync(int requestId, string? note = null);
    Task<bool> DenyRequestAsAdminAsync(int requestId, string reason);
    // UI overlay support: return statuses for a set of media ids
    Task<Dictionary<string, RequestStatus>> GetMyRequestStatusesAsync(IEnumerable<(int mediaId, MediaType mediaType)> items);
}

public interface INotificationService
{
    // Persist a notification and push it live via the in-process broker.
    Task RequestCreatedAsync(MediaRequestDto request);
    Task RequestApprovedAsync(MediaRequestDto request);
    Task RequestRejectedAsync(MediaRequestDto request);
    Task RequestAvailableAsync(MediaRequestDto request);
    Task RequestFailedAsync(MediaRequestDto request, string reason);

    // Persistence-backed reads for the notification bell (survive page refresh/restart).
    Task<List<NotificationDto>> GetForUserAsync(int userId, int take = 20);
    Task<int> GetUnreadCountAsync(int userId);
    Task MarkAllReadAsync(int userId);
    Task MarkReadAsync(int notificationId, int userId);

    // External channels (e.g., Discord) can be triggered here as well in the implementation
}

public interface IPlexAuthService
{
    Task<PlexAuthenticationFlow> BeginAuthenticationAsync();
    Task<bool> OpenAuthenticationWindowAsync(string authUrl);
    Task<PlexAuthenticationResult> PollForAuthenticationAsync(int pinId, CancellationToken cancellationToken = default);
    Task<PlexUser?> GetPlexUserAsync(string authToken);
    Task<int?> GetStoredPinIdAsync();
    Task ClearStoredPinIdAsync();
}

public interface IPlexApiService
{
    Task<List<MediaCardDto>> SearchMediaAsync(string query, MediaType? mediaType = null);
    Task<MediaDetailDto?> GetMediaDetailsAsync(int mediaId, MediaType mediaType);
    Task<List<MediaCardDto>> GetLibraryContentAsync(MediaType mediaType, int page = 1, int pageSize = 20);
    Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10);
    // Discovery feeds (delegate to the metadata provider's real trending/popular/genre endpoints).
    Task<List<MediaCardDto>> GetTrendingAsync(MediaType? mediaType = null, int page = 1, int pageSize = 20);
    Task<List<MediaCardDto>> GetPopularAsync(MediaType mediaType, int page = 1, int pageSize = 20);
    Task<List<MediaCardDto>> GetTopRatedAsync(MediaType mediaType, int page = 1, int pageSize = 20);
    Task<List<MediaCardDto>> GetByGenreAsync(MediaType mediaType, string genre, int page = 1, int pageSize = 20);
    Task<List<MediaCardDto>> GetSimilarAsync(int mediaId, MediaType mediaType, int count = 12);
    Task<bool> IsAvailableOnPlexAsync(int mediaId, MediaType mediaType);
    Task<List<int>> GetAvailableSeasonsAsync(int tvShowId);
    Task<List<EpisodeDto>> GetSeasonEpisodesAsync(int showId, int seasonNumber);
    Task<PlexServerInfo?> GetServerInfoAsync();
    Task<List<PlexLibrary>> GetLibrariesAsync();
    Task AnnotateAvailabilityAsync(List<MediaCardDto> items);
    // Diagnostics
    Task<object> GetIndexStatsAsync();
    Task<object> TestMatchAsync(string? title, int? year, int? tmdbId, string? imdbId, int? tvdbId, MediaType mediaType);
    // Low-level helpers for first-success debugging
    Task<string> GetSectionsRawAsync();
    Task<object> GetMetadataAsync(string ratingKey);
    Task<List<object>> SearchServerAsync(string query, MediaType? mediaType);
    Task<List<object>> ResolveByTitleAsync(string title, int? year, MediaType mediaType, int maxResults = 5);
    Task<object> RebuildAvailabilityIndexAsync();
    Task<object> RebuildAvailabilityFromPlexAsync();
}

public interface IAuthService
{
    Task<AuthenticationResult> SignInWithPlexAsync(string plexToken);
    Task SignOutAsync();
    Task<bool> RefreshTokenAsync();
    Task<bool> IsAuthenticatedAsync();
    Task<UserDto?> GetCurrentUserAsync();
}

public interface IUserProfileService
{
    Task<UserDto?> GetProfileAsync();
    Task<bool> UpdateProfileAsync(UserDto profile);
    Task<UserPreferencesDto> GetPreferencesAsync();
    Task<bool> UpdatePreferencesAsync(UserPreferencesDto preferences);
    Task<bool> UpdateAvatarAsync(string avatarUrl);

    // Admin user management
    Task<List<UserDto>> GetAllUsersAsync();
    Task<bool> SetAdminAsync(int userId, bool isAdmin);
    Task<bool> SetAutoApproveAsync(int userId, bool autoApprove);
}

public interface IDiscordLinkService
{
    /// <summary>Mint a one-time code (shown on the Profile page) that the bot's /request link consumes.</summary>
    string GenerateLinkCode(int userId);
    Task<BridgeLinkResultDto> CompleteLinkAsync(string code, string discordUserId, string? discordUsername);
    Task<BridgeLinkStatusDto> GetStatusByDiscordIdAsync(string discordUserId);
    Task<int?> ResolveUserIdAsync(string discordUserId);
    Task<bool> IsAdminAsync(string discordUserId);
    Task<bool> SetDmOptInAsync(int userId, bool optIn);
}

public interface IToastService
{
    Task ShowSuccessAsync(string message, string? title = null);
    Task ShowErrorAsync(string message, string? title = null);
    Task ShowWarningAsync(string message, string? title = null);
    Task ShowInfoAsync(string message, string? title = null);
}

public interface IThemeService
{
    Task<bool> GetDarkModeAsync();
    Task SetDarkModeAsync(bool isDark);
    Task<ThemeSettings> GetThemeSettingsAsync();
    Task SaveThemeSettingsAsync(ThemeSettings settings);
    event EventHandler<bool> DarkModeChanged;
}

public record ThemeSettings
{
    public bool DarkMode { get; init; } = true;
    public string PrimaryColor { get; init; } = "#E50914";
    public string SecondaryColor { get; init; } = "#221F1F";
    public string FontFamily { get; init; } = "Netflix Sans, Helvetica Neue, sans-serif";
}

public record PlexServerInfo
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
    public int LibraryCount { get; init; }
}

public record PlexLibrary
{
    public int Id { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public MediaType Type { get; init; }
    public int ItemCount { get; init; }
}

public class PlexAuthenticationFlow
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int PinId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string AuthenticationUrl { get; set; } = string.Empty;
    public string ClientIdentifier { get; set; } = string.Empty;
}

public class PlexAuthenticationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string AuthToken { get; set; } = string.Empty;
    public PlexUser? User { get; set; }
}

public class PlexUser
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Thumb { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool HasPassword { get; set; }
}

public class AuthenticationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public UserDto? User { get; set; }
    public List<string> Roles { get; set; } = new();
}
