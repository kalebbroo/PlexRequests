using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Abstractions;

public interface IMediaRequestService
{
    Task<PagedResult<MediaRequestDto>> GetRequestsAsync(MediaFilterDto filter);
    Task<MediaRequestDto?> GetRequestByIdAsync(int id);
    Task<MediaRequestResult> RequestMediaAsync(int mediaId, MediaType mediaType);
    Task<bool> CancelRequestAsync(int requestId);
    Task<bool> IsInWatchlistAsync(int mediaId);
    Task<bool> AddToWatchlistAsync(int mediaId);
    Task<bool> RemoveFromWatchlistAsync(int mediaId);
    Task<List<MediaCardDto>> GetWatchlistAsync();
    Task<UserStatsDto> GetMyStatsAsync();
    Task<bool> CheckRequestLimitsAsync(MediaType mediaType);
}

public interface IPlexAuthService
{
    Task<PlexAuthenticationFlow> BeginAuthenticationAsync();
    Task<bool> OpenAuthenticationWindowAsync(string authUrl);
    Task<PlexAuthenticationResult> PollForAuthenticationAsync(int pinId, CancellationToken cancellationToken = default);
    Task<PlexUser?> GetPlexUserAsync(string authToken);
}

public interface IPlexApiService
{
    Task<List<MediaCardDto>> SearchMediaAsync(string query, MediaType? mediaType = null);
    Task<MediaDetailDto?> GetMediaDetailsAsync(int mediaId, MediaType mediaType);
    Task<List<MediaCardDto>> GetLibraryContentAsync(MediaType mediaType, int page = 1, int pageSize = 20);
    Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10);
    Task<bool> IsAvailableOnPlexAsync(int mediaId, MediaType mediaType);
    Task<List<int>> GetAvailableSeasonsAsync(int tvShowId);
    Task<PlexServerInfo?> GetServerInfoAsync();
    Task<List<PlexLibrary>> GetLibrariesAsync();
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
