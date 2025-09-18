using System.Security.Claims;
using System.Text.Json;
using Blazored.SessionStorage;
using Microsoft.AspNetCore.Components.Authorization;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Services.Abstractions;

namespace PlexRequestsHosted.Services.Auth;

public class CustomAuthStateProvider(ISessionStorageService sessionStorage, HttpClient httpClient, ILogger<CustomAuthStateProvider> logger)
    : AuthenticationStateProvider
{
    private readonly ISessionStorageService _sessionStorage = sessionStorage;
    private readonly HttpClient _httpClient = httpClient;
    private readonly ILogger<CustomAuthStateProvider> _logger = logger;

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

            // Very naive token handling just for scaffolding
            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "User") }, "jwt");
            var user = new ClaimsPrincipal(identity);
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            return new AuthenticationState(user);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetAuthenticationState failed");
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
            _logger.LogError(ex, "AuthenticateWithPlex failed");
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
