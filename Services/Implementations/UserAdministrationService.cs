using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>Admin-only mutations and reporting for users and reusable access groups.</summary>
public sealed class UserAdministrationService(AppDbContext db, IUserAccessService access, IConfiguration configuration) : IUserAdministrationService
{
    public async Task<List<AdminUserDto>> GetUsersAsync()
    {
        if (!await CallerIsAdminAsync()) return [];

        var profiles = await db.UserProfiles.AsNoTracking().Include(x => x.User).Include(x => x.UserGroup)
            .Where(x => x.User != null).OrderBy(x => x.User!.Username).ToListAsync();
        var now = DateTime.UtcNow;
        var earliest = now.AddDays(-365);
        var requests = await db.MediaRequests.AsNoTracking()
            .Where(x => x.RequestedByUserId != null && x.RequestedAt >= earliest
                        && x.Status != RequestStatus.Cancelled && x.Status != RequestStatus.Rejected)
            .Select(x => new { UserId = x.RequestedByUserId!.Value, x.MediaType, x.RequestedAt })
            .ToListAsync();

        var result = new List<AdminUserDto>(profiles.Count);
        foreach (var profile in profiles)
        {
            var effective = Effective(profile);
            result.Add(new AdminUserDto
            {
                Id = profile.UserId,
                Username = profile.User!.Username,
                DisplayName = profile.User.DisplayName,
                Email = profile.User.Email ?? string.Empty,
                AvatarUrl = profile.User.AvatarUrl,
                PlexUsername = profile.PlexUsername,
                PlexId = profile.PlexId,
                PlexLinked = !string.IsNullOrWhiteSpace(profile.PlexId),
                Roles = effective.IsAdmin ? ["User", "Admin"] : ["User"],
                AutoApprove = effective.AutoApprove,
                GroupId = profile.UserGroupId,
                GroupName = profile.UserGroup?.Name,
                CustomPermissions = CustomPermissions(profile),
                EffectivePermissions = effective.Permissions,
                CustomAllowedQualityProfileIds = ParseIds(profile.AllowedQualityProfileIdsCsv),
                EffectiveAllowedQualityProfileIds = ParseIds(effective.AllowedQualityProfileIdsCsv),
                CustomMovieRequestLimitDays = UserAccessService.NormalizeDays(profile.MovieRequestLimitDays),
                CustomTvRequestLimitDays = UserAccessService.NormalizeDays(profile.TvRequestLimitDays),
                CustomMusicRequestLimitDays = UserAccessService.NormalizeDays(profile.MusicRequestLimitDays),
                MovieRequestLimit = profile.MovieRequestLimit,
                TvRequestLimit = profile.TvRequestLimit,
                MusicRequestLimit = profile.MusicRequestLimit,
                LastLoginAt = profile.LastLoginAt,
                ProfileCreatedAt = profile.CreatedAt,
                MovieQuota = Quota(effective.MovieRequestLimit, effective.MovieRequestLimitDays,
                    requests.Count(x => x.UserId == profile.UserId && x.MediaType == MediaType.Movie
                                        && x.RequestedAt >= now.AddDays(-effective.MovieRequestLimitDays))),
                TvQuota = Quota(effective.TvRequestLimit, effective.TvRequestLimitDays,
                    requests.Count(x => x.UserId == profile.UserId && x.MediaType is MediaType.TvShow or MediaType.Anime
                                        && x.RequestedAt >= now.AddDays(-effective.TvRequestLimitDays))),
                MusicQuota = Quota(effective.MusicRequestLimit, effective.MusicRequestLimitDays,
                    requests.Count(x => x.UserId == profile.UserId && x.MediaType == MediaType.Music
                                        && x.RequestedAt >= now.AddDays(-effective.MusicRequestLimitDays)))
            });
        }
        return result;
    }

    public async Task<List<UserGroupDto>> GetGroupsAsync()
    {
        if (!await CallerIsAdminAsync()) return [];
        return await db.UserGroups.AsNoTracking().OrderBy(x => x.Name).Select(x => new UserGroupDto
        {
            Id = x.Id, Name = x.Name, Description = x.Description,
            Permissions = (UserPermission)x.Permissions,
            MovieRequestLimit = x.MovieRequestLimit, MovieRequestLimitDays = x.MovieRequestLimitDays,
            TvRequestLimit = x.TvRequestLimit, TvRequestLimitDays = x.TvRequestLimitDays,
            MusicRequestLimit = x.MusicRequestLimit, MusicRequestLimitDays = x.MusicRequestLimitDays,
            IsSystem = x.IsSystem, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt,
            MemberCount = x.Members.Count,
            AllowedQualityProfileIds = ParseIds(x.AllowedQualityProfileIdsCsv)
        }).ToListAsync();
    }

    public async Task<UserAdminResult> UpdateUserAccessAsync(UserAccessEditDto edit)
    {
        if (!await CallerIsAdminAsync()) return UserAdminResult.Fail("Administrator access is required.");
        var profile = await db.UserProfiles.Include(x => x.UserGroup).Include(x => x.User)
            .FirstOrDefaultAsync(x => x.UserId == edit.UserId);
        if (profile is null) return UserAdminResult.Fail("User not found.");
        if (!ValidQuota(edit.MovieRequestLimit, edit.MovieRequestLimitDays)
            || !ValidQuota(edit.TvRequestLimit, edit.TvRequestLimitDays)
            || !ValidQuota(edit.MusicRequestLimit, edit.MusicRequestLimitDays))
            return UserAdminResult.Fail("Limits must be positive and quota windows must be between 1 and 365 days.");

        UserGroupEntity? group = null;
        if (edit.GroupId is int groupId)
        {
            group = await db.UserGroups.FirstOrDefaultAsync(x => x.Id == groupId);
            if (group is null) return UserAdminResult.Fail("The selected group no longer exists.");
        }

        var wasAdmin = Effective(profile).IsAdmin;
        var custom = UserAccessService.Normalize(edit.CustomPermissions);
        var willBeAdmin = IsConfiguredAdmin(profile.User?.Username) || (group is not null
            ? UserAccessService.Normalize((UserPermission)group.Permissions).HasFlag(UserPermission.Administrator)
            : custom.HasFlag(UserPermission.Administrator));
        if (wasAdmin && !willBeAdmin && !await HasAnotherAdminAsync(profile.UserId))
            return UserAdminResult.Fail("At least one administrator must remain.");

        profile.UserGroupId = group?.Id;
        profile.Permissions = (int)custom;
        profile.AutoApprove = custom.HasFlag(UserPermission.AutoApprove);
        profile.Roles = SetAdminRole(profile.Roles, willBeAdmin || IsConfiguredAdmin(profile.User?.Username));
        profile.MovieRequestLimit = edit.MovieRequestLimit;
        profile.MovieRequestLimitDays = edit.MovieRequestLimitDays;
        profile.TvRequestLimit = edit.TvRequestLimit;
        profile.TvRequestLimitDays = edit.TvRequestLimitDays;
        profile.MusicRequestLimit = edit.MusicRequestLimit;
        profile.MusicRequestLimitDays = edit.MusicRequestLimitDays;
        profile.AllowedQualityProfileIdsCsv = ToCsv(edit.CustomAllowedQualityProfileIds);
        await db.SaveChangesAsync();
        return UserAdminResult.Ok($"Access updated for {profile.User?.Username ?? "user"}.");
    }

    public async Task<UserAdminResult> SaveGroupAsync(UserGroupDto dto)
    {
        if (!await CallerIsAdminAsync()) return UserAdminResult.Fail("Administrator access is required.");
        var name = dto.Name?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 64) return UserAdminResult.Fail("Group names must be 2–64 characters.");
        if (dto.Description?.Trim().Length > 256) return UserAdminResult.Fail("Descriptions cannot exceed 256 characters.");
        if (!ValidQuota(dto.MovieRequestLimit, dto.MovieRequestLimitDays)
            || !ValidQuota(dto.TvRequestLimit, dto.TvRequestLimitDays)
            || !ValidQuota(dto.MusicRequestLimit, dto.MusicRequestLimitDays))
            return UserAdminResult.Fail("Limits must be positive and quota windows must be between 1 and 365 days.");
        if (await db.UserGroups.AnyAsync(x => x.Id != dto.Id && x.Name.ToLower() == name.ToLower()))
            return UserAdminResult.Fail("A group with that name already exists.");

        var entity = dto.Id == 0 ? new UserGroupEntity { CreatedAt = DateTime.UtcNow } :
            await db.UserGroups.FirstOrDefaultAsync(x => x.Id == dto.Id);
        if (entity is null) return UserAdminResult.Fail("Group not found.");
        if (entity.IsSystem && !entity.Name.Equals(name, StringComparison.Ordinal))
            return UserAdminResult.Fail("Built-in group names cannot be changed.");
        var oldPermissions = UserAccessService.Normalize((UserPermission)entity.Permissions);
        var permissions = UserAccessService.Normalize(dto.Permissions);
        if (entity.Id > 0 && oldPermissions.HasFlag(UserPermission.Administrator)
            && !permissions.HasFlag(UserPermission.Administrator))
        {
            var affected = await db.UserProfiles.Include(x => x.User).Where(x => x.UserGroupId == entity.Id).ToListAsync();
            foreach (var member in affected)
                if (!IsConfiguredAdmin(member.User?.Username) && !await HasAnotherAdminAsync(member.UserId, entity.Id))
                    return UserAdminResult.Fail("This change would remove the last administrator.");
        }

        entity.Name = name;
        entity.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        entity.Permissions = (int)permissions;
        entity.MovieRequestLimit = dto.MovieRequestLimit;
        entity.MovieRequestLimitDays = dto.MovieRequestLimitDays;
        entity.TvRequestLimit = dto.TvRequestLimit;
        entity.TvRequestLimitDays = dto.TvRequestLimitDays;
        entity.MusicRequestLimit = dto.MusicRequestLimit;
        entity.MusicRequestLimitDays = dto.MusicRequestLimitDays;
        entity.AllowedQualityProfileIdsCsv = ToCsv(dto.AllowedQualityProfileIds);
        entity.UpdatedAt = DateTime.UtcNow;
        if (dto.Id == 0) db.UserGroups.Add(entity);
        await db.SaveChangesAsync();
        if (entity.Id > 0)
        {
            var groupIsAdmin = permissions.HasFlag(UserPermission.Administrator);
            var members = await db.UserProfiles.Include(x => x.User).Where(x => x.UserGroupId == entity.Id).ToListAsync();
            foreach (var member in members)
                member.Roles = SetAdminRole(member.Roles, groupIsAdmin || IsConfiguredAdmin(member.User?.Username));
            await db.SaveChangesAsync();
        }
        return UserAdminResult.Ok($"Group {entity.Name} saved.");
    }

    public async Task<UserAdminResult> DeleteGroupAsync(int groupId)
    {
        if (!await CallerIsAdminAsync()) return UserAdminResult.Fail("Administrator access is required.");
        var group = await db.UserGroups.Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == groupId);
        if (group is null) return UserAdminResult.Fail("Group not found.");
        if (group.IsSystem) return UserAdminResult.Fail("Built-in groups cannot be deleted.");
        if (group.Members.Count != 0) return UserAdminResult.Fail("Move this group's users before deleting it.");
        db.UserGroups.Remove(group);
        await db.SaveChangesAsync();
        return UserAdminResult.Ok($"Group {group.Name} deleted.");
    }

    private async Task<bool> CallerIsAdminAsync()
    {
        var id = await access.GetCurrentUserIdAsync();
        return id is int userId && await access.IsAdminAsync(userId);
    }

    private async Task<bool> HasAnotherAdminAsync(int excludedUserId, int? groupBeingDemoted = null)
    {
        var profiles = await db.UserProfiles.AsNoTracking().Include(x => x.User).Include(x => x.UserGroup)
            .Where(x => x.UserId != excludedUserId).ToListAsync();
        return profiles.Any(x => x.UserGroupId != groupBeingDemoted && Effective(x).IsAdmin);
    }

    private UserAccessSnapshot Effective(UserProfileEntity profile)
        => UserAccessService.FromProfile(profile, IsConfiguredAdmin(profile.User?.Username));

    private static UserPermission CustomPermissions(UserProfileEntity profile)
    {
        var permissions = (UserPermission)profile.Permissions;
        if (profile.AutoApprove) permissions |= UserPermission.AutoApprove;
        if (UserAccessService.HasRole(profile.Roles, "Admin")) permissions |= UserPermission.Administrator;
        return UserAccessService.Normalize(permissions);
    }

    private static RequestQuotaDto Quota(int? limit, int days, int used) =>
        new() { Limit = limit, WindowDays = days, Used = used };

    private static bool ValidQuota(int? limit, int days) => limit is null or >= 1 && days is >= 1 and <= 365;

    private static string SetAdminRole(string? roles, bool isAdmin)
    {
        var set = (roles ?? "User").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        set.Add("User");
        if (isAdmin) set.Add("Admin"); else set.Remove("Admin");
        return string.Join(',', set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    private bool IsConfiguredAdmin(string? username)
    {
        var configured = configuration["Admin:Usernames"];
        return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(configured)
            && configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(username, StringComparer.OrdinalIgnoreCase);
    }

    private static List<int> ParseIds(string? csv) => string.IsNullOrWhiteSpace(csv) ? [] : csv
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => int.TryParse(x, out var id) ? id : 0).Where(x => x > 0).Distinct().Order().ToList();

    private static string? ToCsv(IEnumerable<int>? ids)
    {
        var values = (ids ?? []).Where(x => x > 0).Distinct().Order().ToList();
        return values.Count == 0 ? null : string.Join(',', values);
    }
}
