using Microsoft.AspNetCore.Components.Authorization;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Auth;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

public class AuthService(AuthenticationStateProvider authProvider) : IAuthService
{
    private readonly CustomAuthStateProvider _provider = (CustomAuthStateProvider)authProvider;

    public async Task<AuthenticationResult> SignInWithPlexAsync(string plexToken)
        => await _provider.AuthenticateWithPlexAsync(plexToken, "plex-user");

    public async Task SignOutAsync() => await _provider.SignOutAsync();

    public Task<bool> RefreshTokenAsync() => Task.FromResult(true);

    public async Task<bool> IsAuthenticatedAsync()
    {
        var state = await _provider.GetAuthenticationStateAsync();
        return state.User.Identity?.IsAuthenticated == true;
    }

    public async Task<UserDto?> GetCurrentUserAsync()
    {
        var state = await _provider.GetAuthenticationStateAsync();
        var name = state.User.Identity?.Name;
        return string.IsNullOrEmpty(name) ? null : new UserDto { Username = name, DisplayName = name };
    }
}
