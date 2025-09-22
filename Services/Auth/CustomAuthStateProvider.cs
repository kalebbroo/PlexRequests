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

namespace PlexRequestsHosted.Services.Auth;

public class CustomAuthStateProvider(
    ISessionStorageService sessionStorage,
    HttpClient httpClient,
    IPlexAuthService plexAuth,
    AppDbContext dbContext)
    : AuthenticationStateProvider
{
    private readonly ISessionStorageService _sessionStorage = sessionStorage;
    private readonly HttpClient _httpClient = httpClient;
    private readonly IPlexAuthService _plexAuth = plexAuth;
    private readonly AppDbContext _db = dbContext;

    private const string AUTH_TOKEN_KEY = "authToken";
    private const string REFRESH_TOKEN_KEY = "refreshToken";
    private const string USER_DATA_KEY = "userData";
    private const string TOKEN_EXPIRY_KEY = "tokenExpiry";

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var token = await _sessionStorage.GetItemAsStringAsync(AUTH_TOKEN_KEY);
            if (string.IsNullOrEmpty(token))
                return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));

            // Load stored user info to rebuild claims (username, email, avatar, display name)
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
                }
            }
            catch { /* ignore and keep minimal identity */ }

            if (!claims.Any()) claims.Add(new Claim(ClaimTypes.Name, "User"));
            var identity = new ClaimsIdentity(claims, "plex");
            var user = new ClaimsPrincipal(identity);
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            return new AuthenticationState(user);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("JavaScript interop calls cannot be issued", StringComparison.OrdinalIgnoreCase))
        {
            // During prerender/static rendering, JS interop (session storage) is unavailable.
            // Return unauthenticated without logging an error to avoid noisy startup failures.
            Logs.Debug($"GetAuthenticationState deferred due to prerender (JS interop unavailable): {ex.Message}");
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }
        catch (Exception ex)
        {
            Logs.Error($"GetAuthenticationState failed: {ex.Message}");
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
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

            // Store token and user info in session
            await _sessionStorage.SetItemAsStringAsync(AUTH_TOKEN_KEY, plexToken);
            await _sessionStorage.SetItemAsync(TOKEN_EXPIRY_KEY, DateTime.UtcNow.AddHours(8));
            var userDto = new UserDto
            {
                Id = existing.Id,
                Username = existing.Username,
                DisplayName = existing.DisplayName,
                Email = existing.Email ?? string.Empty,
                AvatarUrl = existing.AvatarUrl
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

            var identity = new ClaimsIdentity(claims, "plex");
            var principal = new ClaimsPrincipal(identity);
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", plexToken);
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
        }
        finally
        {
            NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
        }
    }
}
