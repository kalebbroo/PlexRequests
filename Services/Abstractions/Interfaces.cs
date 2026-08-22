using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Services.Abstractions;

public interface IMediaRequestService
{
    /// <summary>The signed-in user's requests. Administrator status does not broaden this personal view.</summary>
    Task<PagedResult<MediaRequestDto>> GetRequestsAsync(MediaFilterDto filter);
    Task<MediaRequestDto?> GetRequestByIdAsync(int id);
    // qualityProfileId is the requester's chosen profile; null means "decide for me" (assignment rules, then
    // the default). It's validated against what that user may actually select before it's honoured.
    Task<MediaRequestResult> RequestMediaAsync(int mediaId, MediaType mediaType, int? qualityProfileId = null);
    Task<MediaRequestResult> RequestMediaAsync(MediaRef mediaRef, MediaRequestScope? scope = null, int? qualityProfileId = null);
    Task<MediaRequestResult> RequestSeasonsAsync(int mediaId, MediaType mediaType, List<int> seasons, int? qualityProfileId = null);
    /// <summary>
    /// Request specific episodes. <paramref name="asUpgrade"/> asks for a better copy of episodes already on
    /// Plex: it bypasses the already-available guard and force-enqueues, which is the only way to ask for a
    /// higher-quality version of something you already have.
    /// </summary>
    Task<MediaRequestResult> RequestEpisodesAsync(int mediaId, MediaType mediaType, List<(int season, int episode)> episodes, int? qualityProfileId = null, bool asUpgrade = false);
    Task<MediaRequestResult> RequestSeriesAsync(int mediaId, MediaType mediaType, int? qualityProfileId = null);
    Task<MediaRequestResult> CreateMonitoredEpisodesAsync(int anchorRequestId, IReadOnlyList<(int season, int episode)> episodes);
    // Compatibility entry point retained while music callers move to RequestMediaAsync(MediaRef, scope).
    Task<MediaRequestResult> RequestMusicAsync(string externalId, string source, string title, string? posterUrl = null);
    // User-scoped variants for callers without a cookie session (Discord bridge).
    Task<MediaRequestResult> RequestMediaForUserAsync(int userId, int mediaId, MediaType mediaType);
    Task<MediaRequestResult> RequestMediaForUserAsync(int userId, MediaRef mediaRef, MediaRequestScope? scope = null,
        int? qualityProfileId = null);
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
    /// <summary>Provider-neutral status overlay keyed by <see cref="MediaRef.StableKey"/>. Supports both
    /// TMDb-backed video and string-backed album, artist, and track identities through one contract.</summary>
    Task<Dictionary<string, RequestStatus>> GetMyRequestStatusesAsync(IEnumerable<MediaRef> items);
}

public interface INotificationService
{
    // Persist a notification and push it live via the in-process broker.
    Task RequestCreatedAsync(MediaRequestDto request);
    Task RequestApprovedAsync(MediaRequestDto request);
    Task RequestRejectedAsync(MediaRequestDto request);
    Task RequestAvailableAsync(MediaRequestDto request);
    Task RequestPartiallyAvailableAsync(MediaRequestDto request, string reason);
    Task RequestFailedAsync(MediaRequestDto request, string reason);
    Task RequestCancelledAsync(MediaRequestDto request);
    /// <summary>A request has been searching a long time with no findable release — notify admins so they
    /// can intervene. The request keeps searching; it is never auto-failed.</summary>
    Task RequestSearchStalledAsync(MediaRequestDto request, int attempts);
    /// <summary>An available title was automatically upgraded to a better-quality release. Notify the requester.</summary>
    Task RequestUpgradedAsync(MediaRequestDto request, Quality newQuality);
    Task RequestReplacedAsync(MediaRequestDto request, string target);

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
    Task<List<MediaCardDto>> SearchMediaAsync(MediaSearchQuery query);
    Task<MediaDetailDto?> GetMediaDetailsAsync(int mediaId, MediaType mediaType);
    Task<MediaDetailDto?> GetMediaDetailsAsync(MediaRef mediaRef);
    Task<List<MediaCardDto>> GetLibraryContentAsync(MediaType mediaType, int page = 1, int pageSize = 20);
    Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10);
    // Discovery feeds (delegate to the metadata provider's real trending/popular/genre endpoints).
    Task<List<MediaCardDto>> GetTrendingAsync(MediaType? mediaType = null, int page = 1, int pageSize = 20);
    Task<List<MediaCardDto>> GetPopularAsync(MediaType mediaType, int page = 1, int pageSize = 20);
    Task<List<MediaCardDto>> GetTopRatedAsync(MediaType mediaType, int page = 1, int pageSize = 20);
    Task<List<MediaCardDto>> GetByGenreAsync(MediaType mediaType, string genre, int page = 1, int pageSize = 20);
    Task<List<MediaCardDto>> GetSimilarAsync(int mediaId, MediaType mediaType, int count = 12);
    Task<List<MediaCardDto>> GetSimilarAsync(MediaRef mediaRef, int count = 12);
    Task<bool> IsAvailableOnPlexAsync(int mediaId, MediaType mediaType);
    Task<List<int>> GetAvailableSeasonsAsync(int tvShowId);
    Task<List<EpisodeDto>> GetSeasonEpisodesAsync(int showId, int seasonNumber);
    // Per-season media-quality summary (dominant resolution/codec/total size) for a show's seasons on Plex.
    Task<Dictionary<int, MediaQualityDto>> GetSeasonQualitySummariesAsync(int tvShowId);
    Task<PlexServerInfo?> GetServerInfoAsync();
    Task<List<PlexLibrary>> GetLibrariesAsync();
    /// <summary>
    /// Admin-facing Plex overview assembled from independent, failure-isolated server queries. Usage
    /// history is cached because it is comparatively expensive; active sessions remain live.
    /// </summary>
    Task<PlexServerDashboard> GetServerDashboardAsync();
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
    Task<object> RebuildAvailabilityFromPlexAsync(CancellationToken ct = default);
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
    /// <summary>
    /// The Plex ratingKey for a show, resolved through every external id we might have for it. The
    /// availability index is keyed on Plex's own guids, and Plex tags a show with whichever provider its
    /// agent used — so a tmdb-only lookup silently returns nothing for a show Plex knows by an IMDb id,
    /// leaving it with an empty season map, permanently "incomplete", and therefore never monitorable.
    /// Null when the show isn't in the index at all.
    /// </summary>
    Task<string?> ResolveRatingKeyAsync(int tmdbShowId, CancellationToken ct = default);

    /// <summary>season -> set of episode numbers already on Plex, from the DB availability index.</summary>
    Task<Dictionary<int, HashSet<int>>> GetPlexEpisodesAsync(int tmdbShowId, CancellationToken ct = default);

    /// <summary>Per-season completeness for every season TMDB knows about (season 0/specials excluded).</summary>
    Task<Dictionary<int, SeasonCompleteness>> EvaluateAsync(int tmdbShowId, CancellationToken ct = default);

    /// <summary>Seasons that are fully complete on Plex (Plex episode count >= TMDB's expected count).</summary>
    Task<List<int>> GetCompleteSeasonsAsync(int tmdbShowId, CancellationToken ct = default);

    /// <summary>True iff every season that has actually aired is complete. Unaired future seasons never block this.</summary>
    Task<bool> IsWholeSeriesSatisfiedAsync(int tmdbShowId, CancellationToken ct = default);
}

/// <param name="CountUnknown">
/// TMDB didn't report how many episodes this season has, so <paramref name="Complete"/> is necessarily false
/// and <paramref name="MissingEpisodes"/> is necessarily empty — we know neither what to expect nor what's
/// absent. Callers must decide what that means for them: fetching treats it as "target the whole season as a
/// pack", while satisfaction treats a season with episodes present as good enough, because otherwise the
/// request could never complete.
/// </param>
public record SeasonCompleteness(int SeasonNumber, int PlexCount, int ExpectedCount, bool Complete, bool Aired, List<int> MissingEpisodes, bool CountUnknown = false);

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

public interface IUserAccessService
{
    Task<int?> GetCurrentUserIdAsync();
    Task<UserAccessSnapshot> GetAccessAsync(int userId);
    Task<bool> CanRequestAsync(int userId, MediaType mediaType);
    Task<bool> IsAdminAsync(int userId);
}

/// <summary>
/// Computes rolling request usage for every caller that displays or enforces a quota. Keeping this in one
/// service prevents the request form, profile, and administrator view from disagreeing about what counts.
/// </summary>
public interface IUserQuotaService
{
    Task<UserQuotaOverviewDto> GetUsageAsync(int userId, UserAccessSnapshot access);
    Task<IReadOnlyDictionary<int, UserQuotaOverviewDto>> GetUsageAsync(IReadOnlyCollection<UserAccessSnapshot> users);
    Task<bool> HasCapacityAsync(int userId, MediaType mediaType, UserAccessSnapshot access);
}

public sealed record UserAccessSnapshot(
    int UserId,
    UserPermission Permissions,
    int? GroupId,
    string? GroupName,
    int? MovieRequestLimit,
    int MovieRequestLimitDays,
    int? TvRequestLimit,
    int TvRequestLimitDays,
    int? MusicRequestLimit,
    int MusicRequestLimitDays,
    string? AllowedQualityProfileIdsCsv,
    UserAccountStatus AccountStatus = UserAccountStatus.Active,
    DateTime? SuspendedUntil = null,
    string? AccountStatusReason = null)
{
    public bool IsActive => AccountStatus == UserAccountStatus.Active;
    public bool IsAdmin => IsActive && Permissions.HasFlag(UserPermission.Administrator);
    public bool AutoApprove => IsAdmin || Permissions.HasFlag(UserPermission.AutoApprove);
}

public interface IUserAdministrationService
{
    Task<List<AdminUserDto>> GetUsersAsync();
    Task<AdminUserActivityDto?> GetUserActivityAsync(int userId);
    Task<List<UserGroupDto>> GetGroupsAsync();
    Task<UserAccessAuditPageDto> GetAuditAsync(int take = 50, long? beforeId = null, string? search = null);
    Task<UserAdminResult> UpdateUserAccessAsync(UserAccessEditDto edit);
    Task<UserAdminResult> SaveGroupAsync(UserGroupDto group);
    Task<UserAdminResult> DeleteGroupAsync(int groupId);
}

public interface IDiscordLinkService
{
    /// <summary>Mint a one-time code (shown on the Profile page) that the bot's /request link consumes.</summary>
    Task<string?> GenerateLinkCodeAsync();
    Task<BridgeLinkResultDto> CompleteLinkAsync(string code, string discordUserId, string? discordUsername);
    /// <summary>Read the Discord connection owned by the currently authenticated user.</summary>
    Task<DiscordLinkStatusDto> GetCurrentStatusAsync();
    Task<BridgeLinkStatusDto> GetStatusByDiscordIdAsync(string discordUserId);
    Task<int?> ResolveUserIdAsync(string discordUserId);
    Task<bool> IsAdminAsync(string discordUserId);
    Task<bool> SetCurrentUserDmOptInAsync(bool optIn);
    Task<bool> UnlinkCurrentUserAsync();
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
    /// <summary>Top-level titles in the section (movies, shows, or artists). Null means Plex could not return the metric.</summary>
    public int? ItemCount { get; init; }
    public string ItemLabel { get; init; } = "titles";
    /// <summary>Useful child total for hierarchical libraries (episodes for TV, tracks for music).</summary>
    public int? ChildItemCount { get; init; }
    public string? ChildItemLabel { get; init; }
    public int? CollectionCount { get; init; }
    public bool IsRefreshing { get; init; }
    public DateTime? LastScannedAt { get; init; }
    public List<PlexMediaPreview> RecentlyAdded { get; init; } = new();
}

public record PlexMediaPreview
{
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public string? ThumbPath { get; init; }
    public DateTime? AddedAt { get; init; }
}

public record PlexSessionInfo
{
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public string Username { get; init; } = string.Empty;
    public string? UserThumbPath { get; init; }
    public string? ThumbPath { get; init; }
    public string? ArtPath { get; init; }
    public int ProgressPercent { get; init; }
    public long PositionMilliseconds { get; init; }
    public long DurationMilliseconds { get; init; }
    public bool IsTranscoding { get; init; }
    public string PlaybackMode { get; init; } = "Direct Play";
    public int? BitrateKbps { get; init; }
    public string? PlayerName { get; init; }
    public string? Device { get; init; }
    public string? Platform { get; init; }
    public string? Location { get; init; }
    public string State { get; init; } = "playing";
}

public record PlexUsageInsights
{
    public int PeriodDays { get; init; } = 30;
    public int PlayCount { get; init; }
    public bool IsAvailable { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public List<PlexTopViewer> TopViewers { get; init; } = new();
    public List<PlexTopTitle> TopTitles { get; init; } = new();
}

public record PlexTopViewer
{
    public string Name { get; init; } = string.Empty;
    public string? ThumbPath { get; init; }
    public int Plays { get; init; }
}

public record PlexTopTitle
{
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public string? ThumbPath { get; init; }
    public int Plays { get; init; }
}

public record PlexServerDashboard
{
    public PlexServerInfo? Server { get; init; }
    public List<PlexLibrary> Libraries { get; init; } = new();
    public List<PlexSessionInfo> Sessions { get; init; } = new();
    public PlexUsageInsights Usage { get; init; } = new();
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
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public UserDto? User { get; set; }
    public List<string> Roles { get; set; } = new();
}
