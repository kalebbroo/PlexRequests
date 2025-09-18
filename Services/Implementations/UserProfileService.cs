using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

public class UserProfileService : IUserProfileService
{
    private UserDto _user = new() { Username = "demo", Email = "demo@example.com" };

    public Task<bool> UpdateAvatarAsync(string avatarUrl)
    { _user.AvatarUrl = avatarUrl; return Task.FromResult(true); }

    public Task<bool> UpdatePreferencesAsync(UserPreferencesDto preferences)
    { _user.Preferences = preferences; return Task.FromResult(true); }

    public Task<bool> UpdateProfileAsync(UserDto profile)
    { _user = profile; return Task.FromResult(true); }

    public Task<UserPreferencesDto> GetPreferencesAsync() => Task.FromResult(_user.Preferences);

    public Task<UserDto?> GetProfileAsync() => Task.FromResult<UserDto?>(_user);
}
