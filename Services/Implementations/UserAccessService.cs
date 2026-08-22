using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Services.Abstractions;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>Single source of truth for group inheritance, custom access, and request quotas.</summary>
public sealed class UserAccessService(AppDbContext db, AuthenticationStateProvider auth, IConfiguration configuration) : IUserAccessService
{
    public async Task<int?> GetCurrentUserIdAsync()
    {
        var principal = (await auth.GetAuthenticationStateAsync()).User;
        if (principal.Identity?.IsAuthenticated != true) return null;
        int? userId = int.TryParse(principal.FindFirst("user_id")?.Value, out var claimedId) && claimedId > 0
            ? claimedId
            : null;
        var username = principal.Identity?.Name;
        userId ??= string.IsNullOrWhiteSpace(username)
            ? null
            : await db.Users.Where(x => x.Username == username).Select(x => (int?)x.Id).FirstOrDefaultAsync();
        if (userId is null) return null;
        return (await GetAccessAsync(userId.Value)).IsActive ? userId : null;
    }

    public async Task<UserAccessSnapshot> GetAccessAsync(int userId)
    {
        var profile = await db.UserProfiles.AsNoTracking().Include(x => x.User).Include(x => x.UserGroup)
            .FirstOrDefaultAsync(x => x.UserId == userId);
        if (profile is null)
            return new UserAccessSnapshot(userId, UserPermission.None, null, null, 0, 7, 0, 7, 0, 7,
                null, UserAccountStatus.Disabled, null, "Account not found.");

        return FromProfile(profile, IsConfiguredAdmin(profile.User?.Username));
    }

    public async Task<bool> CanRequestAsync(int userId, MediaType mediaType)
    {
        var access = await GetAccessAsync(userId);
        if (!access.IsActive) return false;
        if (access.IsAdmin) return true;
        var required = mediaType switch
        {
            MediaType.Movie => UserPermission.RequestMovies,
            MediaType.TvShow or MediaType.Anime => UserPermission.RequestTv,
            MediaType.Music => UserPermission.RequestMusic,
            _ => UserPermission.None
        };
        return required != UserPermission.None && access.Permissions.HasFlag(required);
    }

    public async Task<bool> IsAdminAsync(int userId) => (await GetAccessAsync(userId)).IsAdmin;

    internal static UserPermission Normalize(UserPermission permissions) =>
        permissions.HasFlag(UserPermission.Administrator) ? permissions | UserPermission.All : permissions;

    internal static int NormalizeDays(int days) => Math.Clamp(days, 1, 365);

    internal static bool HasRole(string? roles, string role) => (roles ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Contains(role, StringComparer.OrdinalIgnoreCase);

    internal static UserAccessSnapshot FromProfile(Infrastructure.Entities.UserProfileEntity profile, bool configuredAdmin)
    {
        var accountStatus = EffectiveAccountStatus(profile, configuredAdmin);
        if (accountStatus != UserAccountStatus.Active)
            return new(profile.UserId, UserPermission.None, profile.UserGroupId, profile.UserGroup?.Name,
                0, 7, 0, 7, 0, 7, null, accountStatus, profile.SuspendedUntil,
                profile.AccountStatusReason);

        if (profile.UserGroup is { } group)
        {
            var groupPermissions = Normalize((UserPermission)group.Permissions);
            if (configuredAdmin) groupPermissions = Normalize(groupPermissions | UserPermission.Administrator);
            return new(profile.UserId, groupPermissions, group.Id, group.Name,
                groupPermissions.HasFlag(UserPermission.Administrator) ? null : group.MovieRequestLimit, NormalizeDays(group.MovieRequestLimitDays),
                groupPermissions.HasFlag(UserPermission.Administrator) ? null : group.TvRequestLimit, NormalizeDays(group.TvRequestLimitDays),
                groupPermissions.HasFlag(UserPermission.Administrator) ? null : group.MusicRequestLimit, NormalizeDays(group.MusicRequestLimitDays),
                group.AllowedQualityProfileIdsCsv, UserAccountStatus.Active);
        }

        var permissions = (UserPermission)profile.Permissions;
        if (profile.AutoApprove) permissions |= UserPermission.AutoApprove;
        if (HasRole(profile.Roles, "Admin") || configuredAdmin) permissions |= UserPermission.Administrator;
        permissions = Normalize(permissions);
        return new(profile.UserId, permissions, null, null,
            permissions.HasFlag(UserPermission.Administrator) ? null : profile.MovieRequestLimit, NormalizeDays(profile.MovieRequestLimitDays),
            permissions.HasFlag(UserPermission.Administrator) ? null : profile.TvRequestLimit, NormalizeDays(profile.TvRequestLimitDays),
            permissions.HasFlag(UserPermission.Administrator) ? null : profile.MusicRequestLimit, NormalizeDays(profile.MusicRequestLimitDays),
            profile.AllowedQualityProfileIdsCsv, UserAccountStatus.Active);
    }

    internal static UserAccountStatus EffectiveAccountStatus(
        Infrastructure.Entities.UserProfileEntity profile,
        bool configuredAdmin,
        DateTime? now = null)
    {
        if (configuredAdmin) return UserAccountStatus.Active;
        if (profile.AccountStatus == UserAccountStatus.Suspended
            && profile.SuspendedUntil is DateTime until && until <= (now ?? DateTime.UtcNow))
            return UserAccountStatus.Active;
        return profile.AccountStatus;
    }

    private bool IsConfiguredAdmin(string? username)
    {
        var configured = configuration["Admin:Usernames"];
        return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(configured)
            && configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(username, StringComparer.OrdinalIgnoreCase);
    }
}
