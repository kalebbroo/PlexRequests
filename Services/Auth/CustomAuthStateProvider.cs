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
    IHttpContextAccessor httpContextAccessor)
    : AuthenticationStateProvider
{
    private readonly ISessionStorageService _sessionStorage = sessionStorage;
    private readonly HttpClient _httpClient = httpClient;
    private readonly IPlexAuthService _plexAuth = plexAuth;
    private readonly AppDbContext _db = dbContext;
    private readonly IHttpContextAccessor _http = httpContextAccessor;

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

            Logs.Debug("GetAuthenticationState: Cache expired or empty, checking session storage...");

            var token = await _sessionStorage.GetItemAsStringAsync(AUTH_TOKEN_KEY);
            if (string.IsNullOrEmpty(token))
            {
                Logs.Debug("GetAuthenticationState: No session token found, checking cookie authentication...");
                // Fallback to cookie principal if present
                var cookieUser = _http.HttpContext?.User;
                if (cookieUser?.Identity?.IsAuthenticated == true)
                {
                    Logs.Info($"GetAuthenticationState: Found authenticated cookie user: {cookieUser.Identity.Name}");
                    var cookieState = new AuthenticationState(cookieUser);
                    _cachedAuthState = cookieState;
                    _cacheExpiry = DateTime.UtcNow.Add(_cacheTimeout);
                    return cookieState;
                }
                Logs.Debug("GetAuthenticationState: No authentication found, returning unauthenticated state");
                var unauthState = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
                _cachedAuthState = unauthState;
                _cacheExpiry = DateTime.UtcNow.Add(_cacheTimeout);
                return unauthState;
            }

            Logs.Debug($"GetAuthenticationState: Found session token, loading user data...");

            // Load stored user info to rebuild claims (username, email, avatar, display name, roles)
            var claims = new List<Claim>();
            try
            {
                var storedUser = await _sessionStorage.GetItemAsync<UserDto>(USER_DATA_KEY);
                if (storedUser is not null)
                {
                    if (!string.IsNullOrEmpty(storedUser.Username)) claims.Add(new Claim(ClaimTypes.Name, storedUser.Username));
                    if (!string.IsNullOrEmpty(storedUser.Email)) claims.Add(new Claim(ClaimTypes.Email, storedUser.Email));
                    if (!string.IsNullOrEmpty(storedUser.DisplayName)) claims.Add(new Claim("display_name", storedUser.DisplayName));
                    if (!string.IsNullOrEmpty(storedUser.AvatarUrl)) claims.Add(new Claim("avatar_url", storedUser.AvatarUrl));
                    if (storedUser.Roles is not null)
                    {
                        foreach (var r in storedUser.Roles)
                        {
                            if (!string.IsNullOrWhiteSpace(r)) claims.Add(new Claim(ClaimTypes.Role, r));
                        }
                    }
                }
            }
            catch { /* ignore and keep minimal identity */ }

            if (!claims.Any()) claims.Add(new Claim(ClaimTypes.Name, "User"));
            var identity = new ClaimsIdentity(claims, "plex");
            var user = new ClaimsPrincipal(identity);
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            
            var authState = new AuthenticationState(user);
            _cachedAuthState = authState;
            _cacheExpiry = DateTime.UtcNow.Add(_cacheTimeout);
            return authState;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("JavaScript interop calls cannot be issued", StringComparison.OrdinalIgnoreCase))
        {
            // During prerender/static rendering, JS interop (session storage) is unavailable.
            // Fall back to the cookie principal if present to prevent redirect loops.
            Logs.Debug($"GetAuthenticationState prerender: JS interop unavailable. Falling back to cookie principal.");
            var cookieUser = _http.HttpContext?.User;
            if (cookieUser?.Identity?.IsAuthenticated == true)
            {
                var cookieState = new AuthenticationState(cookieUser);
                _cachedAuthState = cookieState;
                _cacheExpiry = DateTime.UtcNow.Add(_cacheTimeout);
                return cookieState;
            }
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
            var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == existing.Id);
            if (profile is null)
            {
                profile = new Infrastructure.Entities.UserProfileEntity
                {
                    UserId = existing.Id,
                    PlexId = plexUser.Id,
                    PlexUsername = plexUser.Username,
                    Roles = "User",
                    LastLoginAt = DateTime.UtcNow
                };
                _db.UserProfiles.Add(profile);
            }
            else
            {
                profile.PlexId = plexUser.Id;
                profile.PlexUsername = plexUser.Username;
                profile.LastLoginAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync();

            // Store token and user info in session
            await _sessionStorage.SetItemAsStringAsync(AUTH_TOKEN_KEY, plexToken);
            await _sessionStorage.SetItemAsync(TOKEN_EXPIRY_KEY, DateTime.UtcNow.AddHours(8));
            var userDto = new UserDto
            {
                Id = existing.Id,
                Username = existing.Username,
                DisplayName = existing.DisplayName,
                Email = existing.Email ?? string.Empty,
                AvatarUrl = existing.AvatarUrl,
                Roles = (profile.Roles ?? "User").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            };
            await _sessionStorage.SetItemAsync(USER_DATA_KEY, userDto);

            // Build claims identity
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, userDto.Username)
            };
            if (!string.IsNullOrEmpty(userDto.Email)) claims.Add(new Claim(ClaimTypes.Email, userDto.Email));
            if (!string.IsNullOrEmpty(userDto.DisplayName)) claims.Add(new Claim("display_name", userDto.DisplayName));
            if (!string.IsNullOrEmpty(userDto.AvatarUrl)) claims.Add(new Claim("avatar_url", userDto.AvatarUrl));
            if (userDto.Roles is not null)
            {
                foreach (var r in userDto.Roles)
                {
                    if (!string.IsNullOrWhiteSpace(r)) claims.Add(new Claim(ClaimTypes.Role, r));
                }
            }

            var identity = new ClaimsIdentity(claims, "plex");
            var principal = new ClaimsPrincipal(identity);
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", plexToken);

            // Try to sign in with the server-side cookie, but don't fail if headers are already sent
            try
            {
                if (_http.HttpContext is not null && !_http.HttpContext.Response.HasStarted)
                {
                    var authProps = new AuthenticationProperties
                    {
                        IsPersistent = true,
                        ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
                    };
                    await _http.HttpContext.SignInAsync("Cookies", principal, authProps);
                    Logs.Info("Cookie sign-in completed");
                }
                else
                {
                    Logs.Info("Cookie sign-in skipped - response already started or no HttpContext");
                }
            }
            catch (Exception ex)
            {
                Logs.Warning($"Cookie SignInAsync failed: {ex.Message}");
            }

            // Clear cache and notify of state change
            _cachedAuthState = null;
            _cacheExpiry = DateTime.MinValue;
            NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(principal)));

            return new AuthenticationResult { Success = true, User = userDto };
        }
        catch (Exception ex)
        {
            Logs.Error($"AuthenticateWithPlex failed: {ex.Message}");
            return new AuthenticationResult { Success = false, ErrorMessage = "Auth failed" };
        }
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
