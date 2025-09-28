using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

public class UserProfileService(AppDbContext db) : IUserProfileService
{
    private readonly AppDbContext _db = db;

    public async Task<UserDto?> GetProfileAsync()
    {
        // Get the most recently logged-in user profile.
        // TODO: This should be updated to get the current authenticated user from context.
        var profile = await _db.UserProfiles
            .OrderByDescending(p => p.LastLoginAt)
            .Include(p => p.User)
            .FirstOrDefaultAsync();

        if (profile?.User == null) return null;

        return MapToDto(profile.User, profile);
    }

    public async Task<bool> UpdateProfileAsync(UserDto profileDto)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == profileDto.Id);
        if (user is null) return false;

        user.DisplayName = profileDto.DisplayName;
        user.Email = profileDto.Email;
        user.AvatarUrl = profileDto.AvatarUrl;

        var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == user.Id);
        if (profile == null)
        {
            profile = new UserProfileEntity { UserId = user.Id };
            _db.UserProfiles.Add(profile);
        }

        // Preferences are updated via UpdatePreferencesAsync
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<UserPreferencesDto> GetPreferencesAsync()
    {
        var profile = await _db.UserProfiles
            .OrderByDescending(p => p.LastLoginAt)
            .FirstOrDefaultAsync();
        return MapPreferences(profile);
    }

    public async Task<bool> UpdatePreferencesAsync(UserPreferencesDto preferences)
    {
        var profile = await _db.UserProfiles
            .OrderByDescending(p => p.LastLoginAt)
            .FirstOrDefaultAsync();

        if (profile == null) return false;

        profile.ThemeDarkMode = preferences.DarkMode;
        profile.Language = preferences.Language;
        profile.DefaultQualityMovie = (int)preferences.DefaultQuality;
        profile.ShowAdultContent = preferences.ShowAdultContent;
        profile.DefaultSort = (int)preferences.DefaultSort;
        profile.AutoplayTrailers = preferences.AutoPlayTrailers;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> UpdateAvatarAsync(string avatarUrl)
    {
        var profile = await _db.UserProfiles
            .OrderByDescending(p => p.LastLoginAt)
            .Include(p => p.User)
            .FirstOrDefaultAsync();
        if (profile?.User == null) return false;
        profile.User.AvatarUrl = avatarUrl;
        await _db.SaveChangesAsync();
        return true;
    }

    private static UserDto MapToDto(UserEntity user, UserProfileEntity profile)
    {
        return new UserDto
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            Email = user.Email ?? string.Empty,
            AvatarUrl = user.AvatarUrl,
            Roles = (profile.Roles ?? "User").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            Preferences = MapPreferences(profile),
            Stats = new UserStatsDto
            {
                // These will be populated by a separate stats service or in Profile page from requests query
            },
            PlexUsername = profile.PlexUsername,
            PlexId = profile.PlexId,
            PlexLinked = !string.IsNullOrEmpty(profile.PlexId),
            MovieRequestLimit = profile.MovieRequestLimit,
            TvRequestLimit = profile.TvRequestLimit,
            MusicRequestLimit = profile.MusicRequestLimit
        };
    }

    private static UserPreferencesDto MapPreferences(UserProfileEntity? profile)
    {
        if (profile == null)
        {
            return new UserPreferencesDto();
        }
        return new UserPreferencesDto
        {
            DarkMode = profile.ThemeDarkMode,
            Language = profile.Language ?? "en",
            EmailNotifications = true,
            PushNotifications = false,
            DefaultQuality = (Shared.Enums.Quality)profile.DefaultQualityMovie,
            ShowAdultContent = profile.ShowAdultContent,
            DefaultSort = (Shared.Enums.SortOrder)profile.DefaultSort,
            AutoPlayTrailers = profile.AutoplayTrailers
        };
    }
}
