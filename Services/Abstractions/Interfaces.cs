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
    Task<MediaRequestResult> RequestSeriesAsync(int mediaId, MediaType mediaType);
    Task<MediaRequestResult> CreateMonitoredEpisodesAsync(int anchorRequestId, IReadOnlyList<(int season, int episode)> episodes);
    // Music (scaffold): request an album/artist by its provider id (MusicBrainz MBID or Plex ratingKey).
    Task<MediaRequestResult> RequestMusicAsync(string externalId, string source, string title, string? posterUrl = null);
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
    Task<PlexAuthenticationFlow> BeginAuthenticationAsync(string? returnUrl = null);
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
    // Admin server health extras
    Task<List<PlexSessionInfo>> GetActiveSessionsAsync();
    Task RefreshLibraryAsync(string sectionKey);
    /// <summary>Resolve the Plex library section key for a media type (first matching section), memoized
    /// briefly since sections rarely change. Null if Plex has no library of that type configured.</summary>
    Task<string?> ResolveSectionKeyAsync(MediaType mediaType);
    Task<PlexAvailabilityStatus> GetAvailabilityStatusAsync();
}

/// <summary>
/// Single source of truth for "is this TV season/series actually complete on Plex" — compares real
/// Plex per-season episode counts against TMDB's expected count, rather than treating "has any episode"
/// as "available". Shared by <see cref="IPlexApiService.GetAvailableSeasonsAsync"/>, the availability
/// reconciliation background service, and the fulfillment queue's missing-target computation, so the
/// definition of "complete" can never drift out of sync between them again.
/// </summary>
public interface ISeasonAvailabilityEvaluator
{
    /// <summary>season -> set of episode numbers already on Plex, from the DB availability index.</summary>
    Task<Dictionary<int, HashSet<int>>> GetPlexEpisodesAsync(int tmdbShowId, CancellationToken ct = default);

    /// <summary>Per-season completeness for every season TMDB knows about (season 0/specials excluded).</summary>
    Task<Dictionary<int, SeasonCompleteness>> EvaluateAsync(int tmdbShowId, CancellationToken ct = default);

    /// <summary>Seasons that are fully complete on Plex (Plex episode count >= TMDB's expected count).</summary>
    Task<List<int>> GetCompleteSeasonsAsync(int tmdbShowId, CancellationToken ct = default);

    /// <summary>True iff every season that has actually aired is complete. Unaired future seasons never block this.</summary>
    Task<bool> IsWholeSeriesSatisfiedAsync(int tmdbShowId, CancellationToken ct = default);
}

public record SeasonCompleteness(int SeasonNumber, int PlexCount, int ExpectedCount, bool Complete, bool Aired, List<int> MissingEpisodes);

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
    public string Platform { get; init; } = string.Empty;
    public string PlatformVersion { get; init; } = string.Empty;
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

public record PlexSessionInfo
{
    public string Title { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public int ProgressPercent { get; init; }
    public bool IsTranscoding { get; init; }
    public int? BitrateKbps { get; init; }
}

public record PlexAvailabilityStatus
{
    public bool Configured { get; init; }
    public DateTime? LastRebuildAt { get; init; }
    public int TitleYearCount { get; init; }
    public int ExternalIdCount { get; init; }
    public int LastMaps { get; init; }
    public int LastSeasons { get; init; }
    public int LastEpisodes { get; init; }
    public int LastPrunedMaps { get; init; }
    public int LastPrunedSeasons { get; init; }
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
