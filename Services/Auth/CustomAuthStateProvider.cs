using System.Security.Claims;
using System.Text.Json;
using Blazored.SessionStorage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Utils;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace PlexRequestsHosted.Services.Auth;

public class CustomAuthStateProvider(
    ISessionStorageService sessionStorage,
    HttpClient httpClient,
    IPlexAuthService plexAuth,
    AppDbContext dbContext,
    IHttpContextAccessor httpContextAccessor,
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    ILogger<CustomAuthStateProvider> logger)
    : AuthenticationStateProvider, IDisposable
{
    private readonly ISessionStorageService _sessionStorage = sessionStorage;
    private readonly HttpClient _httpClient = httpClient;
    private readonly IPlexAuthService _plexAuth = plexAuth;
    private readonly AppDbContext _db = dbContext;
    private readonly IHttpContextAccessor _http = httpContextAccessor;
    private readonly IConfiguration _config = configuration;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<CustomAuthStateProvider> _logger = logger;

    /// <summary>
    /// True when the given Plex username is listed in the ADMIN_USERNAMES / Admin:Usernames
    /// config (comma-separated). Checked on every login so a configured admin self-heals.
    /// </summary>
    private bool IsConfiguredAdmin(string username)
    {
        var raw = _config["Admin:Usernames"];
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(username)) return false;
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Any(u => u.Equals(username, StringComparison.OrdinalIgnoreCase));
    }

    private static string EnsureRole(string? roles, string role)
    {
        var set = (roles ?? "User")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        set.Add("User");
        set.Add(role);
        return string.Join(",", set);
    }

    private const string AUTH_TOKEN_KEY = "authToken";
    private const string REFRESH_TOKEN_KEY = "refreshToken";
    private const string USER_DATA_KEY = "userData";
    private const string TOKEN_EXPIRY_KEY = "tokenExpiry";

    private AuthenticationState? _cachedAuthState;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly TimeSpan _cacheTimeout = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _revalidationCancellation = new();
    private readonly SemaphoreSlim _revalidationLock = new(1, 1);
    private Task? _revalidationTask;
    private int _disposed;

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            // Use cached state if still valid to prevent rapid state changes
            if (_cachedAuthState != null && DateTime.UtcNow < _cacheExpiry)
            {
                Logs.Debug($"GetAuthenticationState: Using cached state (expires in {(_cacheExpiry - DateTime.UtcNow).TotalSeconds:F1}s), authenticated: {_cachedAuthState.User.Identity?.IsAuthenticated == true}");
                return _cachedAuthState;
            }

            Logs.Debug("GetAuthenticationState: Cache expired or empty, checking cookie authentication...");

            // ONLY use cookie authentication - no more session storage confusion
            var cookieUser = _http.HttpContext?.User;
            if (cookieUser?.Identity?.IsAuthenticated == true)
            {
                Logs.Info($"GetAuthenticationState: Found authenticated cookie user: {cookieUser.Identity.Name}");
                
                // Set HTTP client auth header from stored token if available
                try
                {
                    var token = await _sessionStorage.GetItemAsStringAsync(AUTH_TOKEN_KEY);
                    if (!string.IsNullOrEmpty(token))
                    {
                        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    }
                }
                catch (InvalidOperationException)
                {
                    // JS interop not available during prerendering - that's fine
                }
                
                var refreshedUser = await RefreshAccessAsync(cookieUser, CancellationToken.None);
                var cookieState = new AuthenticationState(refreshedUser);
                _cachedAuthState = cookieState;
                _cacheExpiry = DateTime.UtcNow.Add(_cacheTimeout);
                StartRevalidationLoop();
                return cookieState;
            }

            // Claims transformation deliberately turns a suspended/disabled cookie into an unauthenticated
            // principal while retaining the account-status claim. Preserve it so the route redirect can
            // explain why access ended instead of showing a generic sign-in screen.
            if (cookieUser?.FindFirst(UserAccessClaimsTransformation.AccountStatusClaim)?.Value
                is "suspended" or "disabled")
            {
                var restrictedState = new AuthenticationState(cookieUser);
                _cachedAuthState = restrictedState;
                _cacheExpiry = DateTime.UtcNow.Add(_cacheTimeout);
                return restrictedState;
            }

            Logs.Debug("GetAuthenticationState: No cookie authentication found, returning unauthenticated state");
            var unauthState = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
            _cachedAuthState = unauthState;
            _cacheExpiry = DateTime.UtcNow.Add(_cacheTimeout);
            return unauthState;
        }
        catch (Exception ex)
        {
            Logs.Error($"GetAuthenticationState failed: {ex.Message}");
            var errorState = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
            _cachedAuthState = errorState;
            _cacheExpiry = DateTime.UtcNow.Add(_cacheTimeout);
            return errorState;
        }
    }

    private void StartRevalidationLoop()
    {
        _revalidationTask ??= RevalidatePeriodicallyAsync(_revalidationCancellation.Token);
    }

    private async Task RevalidatePeriodicallyAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var previous = _cachedAuthState;
                if (previous?.User.Identity?.IsAuthenticated != true) continue;

                var refreshed = await RefreshAccessAsync(previous.User, cancellationToken);
                var accessChanged = previous.User.Identity?.IsAuthenticated != refreshed.Identity?.IsAuthenticated
                                    || previous.User.IsInRole("Admin") != refreshed.IsInRole("Admin")
                                    || previous.User.FindFirst(UserAccessClaimsTransformation.AccountStatusClaim)?.Value
                                       != refreshed.FindFirst(UserAccessClaimsTransformation.AccountStatusClaim)?.Value;
                _cachedAuthState = new AuthenticationState(refreshed);
                _cacheExpiry = DateTime.UtcNow.Add(_cacheTimeout);
                if (accessChanged)
                {
                    _logger.LogInformation("Live access changed for user {Username}; refreshing the active session",
                        refreshed.Identity?.Name);
                    NotifyAuthenticationStateChanged(Task.FromResult(_cachedAuthState));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The loop is a live-update enhancement; request-time transformation and service guards remain
            // authoritative. Log the failure without taking down the user's Blazor circuit.
            _logger.LogWarning(ex, "The live access revalidation loop stopped unexpectedly");
        }
    }

    private async Task<ClaimsPrincipal> RefreshAccessAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (!int.TryParse(principal.FindFirst("user_id")?.Value, out var userId) || userId <= 0)
            return principal;

        await _revalidationLock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var access = scope.ServiceProvider.GetRequiredService<IUserAccessService>();
            var snapshot = await access.GetAccessAsync(userId);
            return UserAccessClaimsTransformation.RefreshAccess(principal, snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            // The host can dispose its root scope just before a circuit provider receives its disposal
            // callback. That is a normal shutdown race, not an authorization failure worth logging.
            return principal;
        }
        catch (Exception ex)
        {
            // Retain the last known principal through a transient DB outage and retry on the next pass.
            _logger.LogWarning(ex, "Could not revalidate the active session for user {UserId}", userId);
            return principal;
        }
        finally
        {
            _revalidationLock.Release();
        }
    }

    public Task<AuthenticationResult> AuthenticateAsDeveloperAsync(
        string username,
        string? displayName = null,
        string? email = null,
        string? avatarUrl = null,
        string roles = "User,Admin",
        string token = "dev-token")
    {
        return AuthenticateWithUserDataAsync(
            username: username,
            displayName: string.IsNullOrWhiteSpace(displayName) ? username : displayName,
            email: email ?? string.Empty,
            avatarUrl: avatarUrl,
            profileRoles: roles,
            token: token,
            plexUserId: "development",
            overwriteProfileRoles: true,
            forceAdmin: ProfileRolesContainAdmin(roles));
    }

    public async Task<AuthenticationResult> AuthenticateWithPlexAsync(string plexToken, string plexUsername)
    {
        try
        {
            // Resolve user info from Plex to persist
            var plexUser = await _plexAuth.GetPlexUserAsync(plexToken) ?? new PlexUser { Username = plexUsername };

            // Upsert user into DB
            var existing = await _db.Users.FirstOrDefaultAsync(u => u.Username == plexUser.Username);
            if (existing is null)
            {
                existing = new UserEntity
                {
                    Username = plexUser.Username,
                    DisplayName = plexUser.Title ?? plexUser.Username,
                    Email = plexUser.Email,
                    AvatarUrl = plexUser.Thumb
                };
                _db.Users.Add(existing);
            }
            else
            {
                existing.DisplayName = plexUser.Title ?? plexUser.Username;
                existing.Email = plexUser.Email;
                existing.AvatarUrl = plexUser.Thumb;
            }
            await _db.SaveChangesAsync();

            // Upsert profile row
            var isConfiguredAdmin = IsConfiguredAdmin(plexUser.Username);
            var profile = await _db.UserProfiles.Include(p => p.UserGroup).FirstOrDefaultAsync(p => p.UserId == existing.Id);
            var configuredRoles = isConfiguredAdmin ? EnsureRole("User", "Admin") : "User";
            if (profile is not null)
            {
                configuredRoles = isConfiguredAdmin
                    ? EnsureRole(profile.Roles, "Admin")
                    : profile.Roles;
                if (isConfiguredAdmin) profile.Roles = configuredRoles;
            }
            await _db.SaveChangesAsync();

            return await AuthenticateWithUserDataAsync(
                existing.Username,
                existing.DisplayName,
                existing.Email ?? string.Empty,
                existing.AvatarUrl,
                configuredRoles,
                plexToken,
                plexUser.Id,
                overwriteProfileRoles: false,
                forceAdmin: isConfiguredAdmin);
        }
        catch (Exception ex)
        {
            Logs.Error($"AuthenticateWithPlex failed: {ex.Message}");
            return new AuthenticationResult { Success = false, ErrorMessage = "Auth failed" };
        }
    }

    private async Task<AuthenticationResult> AuthenticateWithUserDataAsync(
        string username,
        string? displayName,
        string? email,
        string? avatarUrl,
        string profileRoles,
        string token,
        string? plexUserId,
        bool overwriteProfileRoles,
        bool forceAdmin = false)
    {
        try
        {
            var now = DateTime.UtcNow;
            var normalizedUsername = username?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedUsername))
            {
                return new AuthenticationResult { Success = false, ErrorMessage = "Invalid username" };
            }

            var roleCsv = NormalizeRoleCsv(profileRoles);
            if (string.IsNullOrWhiteSpace(roleCsv))
            {
                roleCsv = "User";
            }

            // Upsert user
            var existing = await _db.Users.FirstOrDefaultAsync(u => u.Username == normalizedUsername);
            if (existing is null)
            {
                existing = new UserEntity
                {
                    Username = normalizedUsername,
                    DisplayName = displayName,
                    Email = email,
                    AvatarUrl = avatarUrl
                };
                _db.Users.Add(existing);
            }
            else
            {
                existing.DisplayName = displayName;
                existing.Email = email;
                existing.AvatarUrl = avatarUrl;
            }
            await _db.SaveChangesAsync();

            // Upsert profile row
            var profile = await _db.UserProfiles.Include(p => p.UserGroup).FirstOrDefaultAsync(p => p.UserId == existing.Id);
            if (profile is null)
            {
                profile = new UserProfileEntity
                {
                    UserId = existing.Id,
                    PlexId = plexUserId,
                    PlexUsername = normalizedUsername,
                    Roles = roleCsv
                };
                _db.UserProfiles.Add(profile);
            }
            else
            {
                var accountStatus = UserAccessService.EffectiveAccountStatus(profile, forceAdmin);
                if (accountStatus != UserAccountStatus.Active)
                {
                    var code = accountStatus == UserAccountStatus.Suspended ? "account_suspended" : "account_disabled";
                    return new AuthenticationResult
                    {
                        Success = false,
                        ErrorCode = code,
                        ErrorMessage = accountStatus == UserAccountStatus.Suspended
                            ? $"This account is temporarily suspended{(profile.SuspendedUntil is DateTime until ? $" until {until:u}" : string.Empty)}."
                            : "This account has been disabled by an administrator."
                    };
                }
                profile.PlexId = plexUserId ?? profile.PlexId;
                profile.PlexUsername = normalizedUsername;
                if (overwriteProfileRoles || string.IsNullOrWhiteSpace(profile.Roles))
                {
                    profile.Roles = roleCsv;
                }
            }
            profile.LastLoginAt = now;
            await _db.SaveChangesAsync();

            // Store token and user info in session storage (best-effort; may fail during server-only callback)
            try
            {
                await _sessionStorage.SetItemAsStringAsync(AUTH_TOKEN_KEY, token);
                await _sessionStorage.SetItemAsync(TOKEN_EXPIRY_KEY, DateTime.UtcNow.AddHours(8));
            }
            catch (Exception ex)
            {
                Logs.Warning($"SessionStorage write skipped (not fatal): {ex.Message}");
            }

            var roles = EffectiveRoles(profile, forceAdmin);
            var userDto = new UserDto
            {
                Id = existing.Id,
                Username = existing.Username,
                DisplayName = existing.DisplayName,
                Email = existing.Email ?? string.Empty,
                AvatarUrl = existing.AvatarUrl,
                Roles = roles
            };
            try
            {
                await _sessionStorage.SetItemAsync(USER_DATA_KEY, userDto);
            }
            catch (Exception ex)
            {
                Logs.Warning($"SessionStorage user write skipped (not fatal): {ex.Message}");
            }

            // Build claims identity
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, userDto.Username),
                new Claim("user_id", existing.Id.ToString())
            };
            if (!string.IsNullOrEmpty(userDto.Email)) claims.Add(new Claim(ClaimTypes.Email, userDto.Email));
            if (!string.IsNullOrEmpty(userDto.DisplayName)) claims.Add(new Claim("display_name", userDto.DisplayName));
            if (!string.IsNullOrEmpty(userDto.AvatarUrl)) claims.Add(new Claim("avatar_url", userDto.AvatarUrl));
            foreach (var r in roles)
            {
                if (!string.IsNullOrWhiteSpace(r)) claims.Add(new Claim(ClaimTypes.Role, r));
            }

            var identity = new ClaimsIdentity(claims, "plex");
            var principal = new ClaimsPrincipal(identity);
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            // CRITICAL: Set cookie authentication FIRST, before any other operations
            if (_http.HttpContext is not null)
            {
                try
                {
                    var authProps = new AuthenticationProperties
                    {
                        IsPersistent = true,
                        ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
                    };
                    await _http.HttpContext.SignInAsync("Cookies", principal, authProps);
                    Logs.Info("Cookie sign-in completed successfully");
                }
                catch (Exception ex)
                {
                    Logs.Error($"Cookie SignInAsync failed: {ex.Message}");
                    return new AuthenticationResult { Success = false, ErrorMessage = "Failed to set authentication cookie" };
                }
            }
            else
            {
                Logs.Error("No HttpContext available for cookie authentication");
                return new AuthenticationResult { Success = false, ErrorMessage = "No HttpContext for authentication" };
            }

            // Clear cache and notify of state change
            _cachedAuthState = null;
            _cacheExpiry = DateTime.MinValue;
            NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(principal)));

            return new AuthenticationResult { Success = true, User = userDto, Roles = roles };
        }
        catch (Exception ex)
        {
            Logs.Error($"AuthenticateWithUserDataAsync failed: {ex.Message}");
            return new AuthenticationResult { Success = false, ErrorMessage = "Auth failed" };
        }
    }

    private static string NormalizeRoleCsv(string? roles)
    {
        if (string.IsNullOrWhiteSpace(roles)) return "User";

        var set = roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        set.Add("User");
        return string.Join(",", set);
    }

    private static bool ProfileRolesContainAdmin(string? roles) => (roles ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Contains("Admin", StringComparer.OrdinalIgnoreCase);

    private static List<string> EffectiveRoles(UserProfileEntity profile, bool forceAdmin)
    {
        var roles = (profile.Roles ?? "User")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        roles.Add("User");
        if (profile.UserGroup is { } group)
        {
            if (((UserPermission)group.Permissions).HasFlag(UserPermission.Administrator)) roles.Add("Admin");
            else if (!forceAdmin) roles.Remove("Admin");
        }
        if (forceAdmin) roles.Add("Admin");
        return roles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task SignOutAsync()
    {
        try
        {
            await _sessionStorage.RemoveItemAsync(AUTH_TOKEN_KEY);
            await _sessionStorage.RemoveItemAsync(REFRESH_TOKEN_KEY);
            await _sessionStorage.RemoveItemAsync(USER_DATA_KEY);
            await _sessionStorage.RemoveItemAsync(TOKEN_EXPIRY_KEY);
            _httpClient.DefaultRequestHeaders.Authorization = null;
            try
            {
                if (_http.HttpContext is not null)
                {
                    await _http.HttpContext.SignOutAsync("Cookies");
                    Logs.Info("Cookie sign-out completed");
                }
            }
            catch (Exception ex)
            {
                Logs.Warning($"Cookie SignOutAsync failed: {ex.Message}");
            }
        }
        finally
        {
            _revalidationCancellation.Cancel();
            _cachedAuthState = null;
            NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
        }
    }

    public void Dispose()
    {
        // The same scoped instance is exposed through both CustomAuthStateProvider and
        // AuthenticationStateProvider registrations, so DI may ask it to dispose more than once.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _revalidationCancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        // Do not synchronously block waiting for the timer task: Dispose is also used by background-job
        // scopes. Cancellation stops it promptly and the task owns no unmanaged resources.
    }
}
