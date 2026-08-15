using System.Security.Claims;
using System.Text.Json;
using Blazored.SessionStorage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Utils;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace PlexRequestsHosted.Services.Auth;

public class CustomAuthStateProvider(
    ISessionStorageService sessionStorage,
    HttpClient httpClient,
    IPlexAuthService plexAuth,
    AppDbContext dbContext,
    IHttpContextAccessor httpContextAccessor,
    IConfiguration configuration)
    : AuthenticationStateProvider
{
    private readonly ISessionStorageService _sessionStorage = sessionStorage;
    private readonly HttpClient _httpClient = httpClient;
    private readonly IPlexAuthService _plexAuth = plexAuth;
    private readonly AppDbContext _db = dbContext;
    private readonly IHttpContextAccessor _http = httpContextAccessor;
    private readonly IConfiguration _config = configuration;

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
                
                var cookieState = new AuthenticationState(cookieUser);
                _cachedAuthState = cookieState;
                _cacheExpiry = DateTime.UtcNow.Add(_cacheTimeout);
                return cookieState;
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
            overwriteProfileRoles: true);
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
            var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == existing.Id);
            var configuredRoles = isConfiguredAdmin ? EnsureRole("User", "Admin") : "User";
            if (profile is not null)
            {
                configuredRoles = isConfiguredAdmin
                    ? EnsureRole(profile.Roles, "Admin")
                    : profile.Roles;
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
                overwriteProfileRoles: false);
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
        bool overwriteProfileRoles)
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
            var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == existing.Id);
            if (profile is null)
            {
                profile = new UserProfileEntity
                {
                    UserId = existing.Id,
                    PlexId = plexUserId,
                    PlexUsername = normalizedUsername,
                    Roles = roleCsv,
                    LastLoginAt = now
                };
                _db.UserProfiles.Add(profile);
            }
            else
            {
                profile.PlexId = plexUserId ?? profile.PlexId;
                profile.PlexUsername = normalizedUsername;
                profile.LastLoginAt = now;
                if (overwriteProfileRoles || string.IsNullOrWhiteSpace(profile.Roles))
                {
                    profile.Roles = roleCsv;
                }
            }
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

            var roles = (profile.Roles ?? "User").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
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
            NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
        }
    }
}
