using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>Admin-only mutations and reporting for users and reusable access groups.</summary>
public sealed class UserAdministrationService(
    AppDbContext db,
    IUserAccessService access,
    IUserQuotaService quotas,
    IConfiguration configuration) : IUserAdministrationService
{
    public async Task<List<AdminUserDto>> GetUsersAsync()
    {
        if (!await CallerIsAdminAsync()) return [];

        // Temporary suspensions are self-healing. Access checks already treat an elapsed suspension as
        // active; repair the persisted display state whenever an admin opens the user list.
        var now = DateTime.UtcNow;
        await db.UserProfiles
            .Where(x => x.AccountStatus == UserAccountStatus.Suspended
                        && x.SuspendedUntil != null && x.SuspendedUntil <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.AccountStatus, UserAccountStatus.Active)
                .SetProperty(x => x.SuspendedUntil, (DateTime?)null)
                .SetProperty(x => x.AccountStatusReason, (string?)null)
                .SetProperty(x => x.AccountStatusChangedAt, now)
                .SetProperty(x => x.AccountStatusChangedBy, "System · suspension expired"));

        var profiles = await db.UserProfiles.AsNoTracking().Include(x => x.User).Include(x => x.UserGroup)
            .Where(x => x.User != null).OrderBy(x => x.User!.Username).ToListAsync();
        var effectiveAccess = profiles.ToDictionary(x => x.UserId, Effective);
        var quotaUsage = await quotas.GetUsageAsync(effectiveAccess.Values.ToList());

        var result = new List<AdminUserDto>(profiles.Count);
        foreach (var profile in profiles)
        {
            var effective = effectiveAccess[profile.UserId];
            var usage = quotaUsage[profile.UserId];
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
                MovieQuota = usage.Movie,
                TvQuota = usage.Tv,
                MusicQuota = usage.Music,
                AccountStatus = effective.AccountStatus,
                SuspendedUntil = effective.SuspendedUntil,
                AccountStatusReason = effective.IsActive ? null : profile.AccountStatusReason,
                AccountStatusChangedAt = profile.AccountStatusChangedAt,
                AccountStatusChangedBy = profile.AccountStatusChangedBy
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

        var reason = edit.AccountStatusReason?.Trim();
        var requestedStatus = edit.AccountStatus;
        DateTime? suspendedUntil = edit.SuspendedUntil is DateTime suppliedUntil
            ? suppliedUntil.Kind == DateTimeKind.Utc ? suppliedUntil : DateTime.SpecifyKind(suppliedUntil, DateTimeKind.Utc)
            : null;
        if (!Enum.IsDefined(requestedStatus))
            return UserAdminResult.Fail("The selected account status is invalid.");
        if (requestedStatus == UserAccountStatus.Suspended
            && (suspendedUntil is null || suspendedUntil <= DateTime.UtcNow || suspendedUntil > DateTime.UtcNow.AddDays(366)))
            return UserAdminResult.Fail("A suspension must end in the future and cannot exceed one year.");
        if (requestedStatus != UserAccountStatus.Active && string.IsNullOrWhiteSpace(reason))
            return UserAdminResult.Fail("Add a reason before restricting an account.");
        if (reason?.Length > 256)
            return UserAdminResult.Fail("Account status reasons cannot exceed 256 characters.");
        if (requestedStatus == UserAccountStatus.Active)
        {
            suspendedUntil = null;
            reason = null;
        }
        else if (requestedStatus == UserAccountStatus.Disabled)
        {
            suspendedUntil = null;
        }
        if (requestedStatus != UserAccountStatus.Active && IsConfiguredAdmin(profile.User?.Username))
            return UserAdminResult.Fail("Configured emergency administrators cannot be suspended or disabled. Remove the username from ADMIN_USERNAMES first.");

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
        var willBeActive = requestedStatus == UserAccountStatus.Active;
        if (wasAdmin && (!willBeAdmin || !willBeActive) && !await HasAnotherAdminAsync(profile.UserId))
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
        var statusChanged = profile.AccountStatus != requestedStatus
                            || profile.SuspendedUntil != suspendedUntil
                            || !string.Equals(profile.AccountStatusReason, reason, StringComparison.Ordinal);
        profile.AccountStatus = requestedStatus;
        profile.SuspendedUntil = suspendedUntil;
        profile.AccountStatusReason = reason;
        if (statusChanged)
        {
            profile.AccountStatusChangedAt = DateTime.UtcNow;
            var callerId = await access.GetCurrentUserIdAsync();
            profile.AccountStatusChangedBy = callerId is int id
                ? await db.Users.Where(x => x.Id == id).Select(x => x.Username).FirstOrDefaultAsync()
                : null;
        }
        await db.SaveChangesAsync();
        return UserAdminResult.Ok($"Access and account status updated for {profile.User?.Username ?? "user"}.");
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
