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

    public async Task<UserAccessAuditPageDto> GetAuditAsync(int take = 50, long? beforeId = null, string? search = null)
    {
        if (!await CallerIsAdminAsync()) return new();
        take = Math.Clamp(take, 1, 100);
        var term = search?.Trim();
        if (term?.Length > 128) term = term[..128];

        var query = db.UserAccessAudits.AsNoTracking();
        if (beforeId is > 0) query = query.Where(x => x.Id < beforeId.Value);
        if (!string.IsNullOrWhiteSpace(term))
        {
            var normalized = term.ToLowerInvariant();
            query = query.Where(x => x.TargetName.ToLower().Contains(normalized)
                                     || x.ActorUsername.ToLower().Contains(normalized)
                                     || x.Summary.ToLower().Contains(normalized));
        }

        var rows = await query.OrderByDescending(x => x.Id).Take(take + 1).ToListAsync();
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new UserAccessAuditPageDto
        {
            Items = rows.Select(x => new UserAccessAuditDto
            {
                Id = x.Id,
                TargetType = x.TargetType,
                TargetId = x.TargetId,
                TargetName = x.TargetName,
                Action = x.Action,
                ActorUserId = x.ActorUserId,
                ActorUsername = x.ActorUsername,
                Summary = x.Summary,
                CreatedAt = x.CreatedAt
            }).ToList(),
            HasMore = hasMore,
            NextCursor = hasMore && rows.Count > 0 ? rows[^1].Id : null
        };
    }

    public async Task<UserAdminResult> UpdateUserAccessAsync(UserAccessEditDto edit)
    {
        if (!await CallerIsAdminAsync()) return UserAdminResult.Fail("Administrator access is required.");
        var profile = await db.UserProfiles.Include(x => x.UserGroup).Include(x => x.User)
            .FirstOrDefaultAsync(x => x.UserId == edit.UserId);
        if (profile is null) return UserAdminResult.Fail("User not found.");
        var before = Capture(profile);
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
        }

        var changes = DescribeUserChanges(before, Capture(profile, group));
        if (changes.Count > 0)
        {
            var actor = await CurrentActorAsync();
            if (statusChanged) profile.AccountStatusChangedBy = actor.Username;
            AddAudit(UserAccessAuditTarget.User, profile.UserId, profile.User?.Username ?? $"User {profile.UserId}",
                UserAccessAuditAction.Updated, actor, changes);
        }
        await db.SaveChangesAsync();
        return UserAdminResult.Ok(changes.Count == 0
            ? $"No access changes were needed for {profile.User?.Username ?? "user"}."
            : $"Access and account status updated for {profile.User?.Username ?? "user"}.");
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
        var before = dto.Id == 0 ? null : Capture(entity);
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
        var actor = await CurrentActorAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (dto.Id == 0) db.UserGroups.Add(entity);
        await db.SaveChangesAsync();
        var memberCount = 0;
        if (entity.Id > 0)
        {
            var groupIsAdmin = permissions.HasFlag(UserPermission.Administrator);
            var members = await db.UserProfiles.Include(x => x.User).Where(x => x.UserGroupId == entity.Id).ToListAsync();
            memberCount = members.Count;
            foreach (var member in members)
                member.Roles = SetAdminRole(member.Roles, groupIsAdmin || IsConfiguredAdmin(member.User?.Username));
        }

        var changes = before is null ? DescribeCreatedGroup(Capture(entity)) : DescribeGroupChanges(before, Capture(entity));
        if (changes.Count > 0)
        {
            if (before is not null) changes.Add($"Affected members: {memberCount}");
            AddAudit(UserAccessAuditTarget.Group, entity.Id, entity.Name,
                before is null ? UserAccessAuditAction.Created : UserAccessAuditAction.Updated, actor, changes);
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return UserAdminResult.Ok(changes.Count == 0 ? $"No changes were needed for {entity.Name}." : $"Group {entity.Name} saved.");
    }

    public async Task<UserAdminResult> DeleteGroupAsync(int groupId)
    {
        if (!await CallerIsAdminAsync()) return UserAdminResult.Fail("Administrator access is required.");
        var group = await db.UserGroups.Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == groupId);
        if (group is null) return UserAdminResult.Fail("Group not found.");
        if (group.IsSystem) return UserAdminResult.Fail("Built-in groups cannot be deleted.");
        if (group.Members.Count != 0) return UserAdminResult.Fail("Move this group's users before deleting it.");
        var actor = await CurrentActorAsync();
        AddAudit(UserAccessAuditTarget.Group, group.Id, group.Name, UserAccessAuditAction.Deleted, actor,
            ["Deleted the access group."]);
        db.UserGroups.Remove(group);
        await db.SaveChangesAsync();
        return UserAdminResult.Ok($"Group {group.Name} deleted.");
    }

    private async Task<bool> CallerIsAdminAsync()
    {
        var id = await access.GetCurrentUserIdAsync();
        return id is int userId && await access.IsAdminAsync(userId);
    }

    private async Task<AuditActor> CurrentActorAsync()
    {
        var id = await access.GetCurrentUserIdAsync();
        var username = id is int userId
            ? await db.Users.Where(x => x.Id == userId).Select(x => x.Username).FirstOrDefaultAsync()
            : null;
        return new AuditActor(id, username ?? "Unknown administrator");
    }

    private void AddAudit(UserAccessAuditTarget targetType, int targetId, string targetName,
        UserAccessAuditAction action, AuditActor actor, IEnumerable<string> changes)
    {
        var summary = string.Join(" · ", changes.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (summary.Length > 2048) summary = summary[..2045] + "...";
        db.UserAccessAudits.Add(new UserAccessAuditEntity
        {
            TargetType = targetType,
            TargetId = targetId,
            TargetName = Clip(targetName, 128),
            Action = action,
            ActorUserId = actor.Id,
            ActorUsername = Clip(actor.Username, 128),
            Summary = summary,
            CreatedAt = DateTime.UtcNow
        });
    }

    private async Task<bool> HasAnotherAdminAsync(int excludedUserId, int? groupBeingDemoted = null)
    {
        var profiles = await db.UserProfiles.AsNoTracking().Include(x => x.User).Include(x => x.UserGroup)
            .Where(x => x.UserId != excludedUserId).ToListAsync();
        return profiles.Any(x => x.UserGroupId != groupBeingDemoted && Effective(x).IsAdmin);
    }

    private UserAccessSnapshot Effective(UserProfileEntity profile)
        => UserAccessService.FromProfile(profile, IsConfiguredAdmin(profile.User?.Username));

    private static UserAuditState Capture(UserProfileEntity profile) => Capture(profile, profile.UserGroup);

    private static UserAuditState Capture(UserProfileEntity profile, UserGroupEntity? assignedGroup) => new(
        assignedGroup?.Name,
        UserAccessService.Normalize((UserPermission)profile.Permissions),
        profile.MovieRequestLimit, UserAccessService.NormalizeDays(profile.MovieRequestLimitDays),
        profile.TvRequestLimit, UserAccessService.NormalizeDays(profile.TvRequestLimitDays),
        profile.MusicRequestLimit, UserAccessService.NormalizeDays(profile.MusicRequestLimitDays),
        NormalizeIds(profile.AllowedQualityProfileIdsCsv),
        profile.AccountStatus, profile.SuspendedUntil, profile.AccountStatusReason?.Trim());

    private static GroupAuditState Capture(UserGroupEntity group) => new(
        group.Name, group.Description?.Trim(), UserAccessService.Normalize((UserPermission)group.Permissions),
        group.MovieRequestLimit, UserAccessService.NormalizeDays(group.MovieRequestLimitDays),
        group.TvRequestLimit, UserAccessService.NormalizeDays(group.TvRequestLimitDays),
        group.MusicRequestLimit, UserAccessService.NormalizeDays(group.MusicRequestLimitDays),
        NormalizeIds(group.AllowedQualityProfileIdsCsv));

    private static List<string> DescribeUserChanges(UserAuditState before, UserAuditState after)
    {
        var changes = new List<string>();
        AddChange(changes, "Access group", before.GroupName ?? "Custom access", after.GroupName ?? "Custom access");
        AddChange(changes, "Custom permissions", PermissionLabel(before.Permissions), PermissionLabel(after.Permissions));
        AddChange(changes, "Movie quota", QuotaLabel(before.MovieLimit, before.MovieDays), QuotaLabel(after.MovieLimit, after.MovieDays));
        AddChange(changes, "TV quota", QuotaLabel(before.TvLimit, before.TvDays), QuotaLabel(after.TvLimit, after.TvDays));
        AddChange(changes, "Music quota", QuotaLabel(before.MusicLimit, before.MusicDays), QuotaLabel(after.MusicLimit, after.MusicDays));
        AddChange(changes, "Allowed qualities", QualityLabel(before.QualityIds), QualityLabel(after.QualityIds));
        AddChange(changes, "Account status", before.AccountStatus.ToString(), after.AccountStatus.ToString());
        AddChange(changes, "Suspension ends", DateLabel(before.SuspendedUntil), DateLabel(after.SuspendedUntil));
        AddChange(changes, "Restriction reason", before.Reason ?? "None", after.Reason ?? "None");
        return changes;
    }

    private static List<string> DescribeCreatedGroup(GroupAuditState group) =>
    [
        $"Created with permissions: {PermissionLabel(group.Permissions)}",
        $"Movie quota: {QuotaLabel(group.MovieLimit, group.MovieDays)}",
        $"TV quota: {QuotaLabel(group.TvLimit, group.TvDays)}",
        $"Music quota: {QuotaLabel(group.MusicLimit, group.MusicDays)}",
        $"Allowed qualities: {QualityLabel(group.QualityIds)}"
    ];

    private static List<string> DescribeGroupChanges(GroupAuditState before, GroupAuditState after)
    {
        var changes = new List<string>();
        AddChange(changes, "Name", before.Name, after.Name);
        AddChange(changes, "Description", before.Description ?? "None", after.Description ?? "None");
        AddChange(changes, "Permissions", PermissionLabel(before.Permissions), PermissionLabel(after.Permissions));
        AddChange(changes, "Movie quota", QuotaLabel(before.MovieLimit, before.MovieDays), QuotaLabel(after.MovieLimit, after.MovieDays));
        AddChange(changes, "TV quota", QuotaLabel(before.TvLimit, before.TvDays), QuotaLabel(after.TvLimit, after.TvDays));
        AddChange(changes, "Music quota", QuotaLabel(before.MusicLimit, before.MusicDays), QuotaLabel(after.MusicLimit, after.MusicDays));
        AddChange(changes, "Allowed qualities", QualityLabel(before.QualityIds), QualityLabel(after.QualityIds));
        return changes;
    }

    private static void AddChange(ICollection<string> changes, string field, string before, string after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal)) changes.Add($"{field}: {before} → {after}");
    }

    private static string PermissionLabel(UserPermission permissions)
    {
        var values = new[]
        {
            permissions.HasFlag(UserPermission.Administrator) ? "Administrator" : null,
            permissions.HasFlag(UserPermission.AutoApprove) ? "Auto-approve" : null,
            permissions.HasFlag(UserPermission.RequestMovies) ? "Movies" : null,
            permissions.HasFlag(UserPermission.RequestTv) ? "TV" : null,
            permissions.HasFlag(UserPermission.RequestMusic) ? "Music" : null
        }.Where(x => x is not null);
        var label = string.Join(", ", values);
        return string.IsNullOrEmpty(label) ? "Browse only" : label;
    }

    private static string QuotaLabel(int? limit, int days) => limit is null ? "Unlimited" : $"{limit} every {days} days";
    private static string QualityLabel(string? ids) => string.IsNullOrWhiteSpace(ids) ? "All selectable" : $"Profile ids {ids}";
    private static string DateLabel(DateTime? value) => value is null ? "None" : value.Value.ToUniversalTime().ToString("u");
    private static string? NormalizeIds(string? csv) => ToCsv(ParseIds(csv));
    private static string Clip(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

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

    private sealed record AuditActor(int? Id, string Username);
    private sealed record UserAuditState(
        string? GroupName,
        UserPermission Permissions,
        int? MovieLimit, int MovieDays,
        int? TvLimit, int TvDays,
        int? MusicLimit, int MusicDays,
        string? QualityIds,
        UserAccountStatus AccountStatus,
        DateTime? SuspendedUntil,
        string? Reason);
    private sealed record GroupAuditState(
        string Name,
        string? Description,
        UserPermission Permissions,
        int? MovieLimit, int MovieDays,
        int? TvLimit, int TvDays,
        int? MusicLimit, int MusicDays,
        string? QualityIds);
}
