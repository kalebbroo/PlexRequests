using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

public class UserProfileService(AppDbContext db, IUserAccessService access) : IUserProfileService
{
    private readonly AppDbContext _db = db;
    private readonly IUserAccessService _access = access;

    public async Task<UserDto?> GetProfileAsync()
    {
        var userId = await _access.GetCurrentUserIdAsync();
        if (userId is null) return null;
        var profile = await _db.UserProfiles
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (profile?.User == null) return null;

        var dto = MapToDto(profile.User, profile);
        var effective = await _access.GetAccessAsync(profile.UserId);
        dto.Roles = effective.IsAdmin ? ["User", "Admin"] : ["User"];
        dto.AutoApprove = effective.AutoApprove;
        dto.MovieRequestLimit = effective.MovieRequestLimit;
        dto.TvRequestLimit = effective.TvRequestLimit;
        dto.MusicRequestLimit = effective.MusicRequestLimit;
        return dto;
    }

    public async Task<bool> UpdateProfileAsync(UserDto profileDto)
    {
        var currentUserId = await _access.GetCurrentUserIdAsync();
        if (currentUserId is null || profileDto.Id != currentUserId.Value) return false;
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == currentUserId.Value);
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
        var userId = await _access.GetCurrentUserIdAsync();
        if (userId is null) return new UserPreferencesDto();
        var profile = await _db.UserProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId.Value);
        return MapPreferences(profile);
    }

    public async Task<bool> UpdatePreferencesAsync(UserPreferencesDto preferences)
    {
        var userId = await _access.GetCurrentUserIdAsync();
        if (userId is null) return false;
        var profile = await _db.UserProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId.Value);

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
        var userId = await _access.GetCurrentUserIdAsync();
        if (userId is null) return false;
        var profile = await _db.UserProfiles
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.UserId == userId.Value);
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
            AutoApprove = profile.AutoApprove,
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
