using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>Restores missing built-in templates without overwriting administrator edits.</summary>
public sealed class UserGroupSeeder(AppDbContext db)
{
    public async Task SeedAsync()
    {
        var existing = await db.UserGroups.Select(x => x.Name).ToListAsync();
        var names = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        foreach (var group in BuiltIns(now).Where(x => !names.Contains(x.Name))) db.UserGroups.Add(group);
        await db.SaveChangesAsync();
    }

    private static IEnumerable<UserGroupEntity> BuiltIns(DateTime now)
    {
        yield return Group("Members", "Standard requests with administrator approval.",
            UserPermission.AllRequests, 5, 3, 10, now);
        yield return Group("Trusted", "Higher rolling quotas with automatic approval.",
            UserPermission.AllRequests | UserPermission.AutoApprove, 20, 10, 25, now);
        yield return Group("Administrators", "Full application access with automatic approval and no quotas.",
            UserPermission.All, null, null, null, now);
        yield return Group("Guests", "Browse-only access without request permission.",
            UserPermission.None, null, null, null, now);
    }

    private static UserGroupEntity Group(string name, string description, UserPermission permissions,
        int? movieLimit, int? tvLimit, int? musicLimit, DateTime now) => new()
    {
        Name = name,
        Description = description,
        Permissions = (int)permissions,
        MovieRequestLimit = movieLimit,
        TvRequestLimit = tvLimit,
        MusicRequestLimit = musicLimit,
        MovieRequestLimitDays = 7,
        TvRequestLimitDays = 7,
        MusicRequestLimitDays = 7,
        IsSystem = true,
        CreatedAt = now,
        UpdatedAt = now
    };
}
