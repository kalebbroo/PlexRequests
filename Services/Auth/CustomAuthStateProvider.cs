using System.Security.Claims;
using System.Text.Json;
using Blazored.SessionStorage;
using Microsoft.AspNetCore.Components.Authorization;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Utils;

namespace PlexRequestsHosted.Services.Auth;

public class CustomAuthStateProvider(ISessionStorageService sessionStorage, HttpClient httpClient)
    : AuthenticationStateProvider
{
    private readonly ISessionStorageService _sessionStorage = sessionStorage;
    private readonly HttpClient _httpClient = httpClient;

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

            // Load stored user info (if available) so we can set a meaningful identity name
            string displayName = "User";
            try
            {
                var plexUser = await _sessionStorage.GetItemAsync<UserDto>(USER_DATA_KEY);
                if (plexUser is not null && !string.IsNullOrWhiteSpace(plexUser.Username))
                    displayName = plexUser.Username;
            }
            catch { /* ignore deserialization issues and stick with default */ }

            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, displayName) }, "jwt");
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
            // For now, trust plexToken and create a fake JWT placeholder
            await _sessionStorage.SetItemAsStringAsync(AUTH_TOKEN_KEY, plexToken);
            await _sessionStorage.SetItemAsync(TOKEN_EXPIRY_KEY, DateTime.UtcNow.AddHours(8));
            await _sessionStorage.SetItemAsync(USER_DATA_KEY, new UserDto { Username = plexUsername, DisplayName = plexUsername });

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, plexUsername)
            }, "jwt");
            var user = new ClaimsPrincipal(identity);
            NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(user)));
            return new AuthenticationResult { Success = true, User = new UserDto { Username = plexUsername } };
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
