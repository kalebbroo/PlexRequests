using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Services.Auth;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class UserAccessControlTests
{
    [Fact]
    public async Task Group_permissions_are_authoritative_for_web_and_discord_access()
    {
        await using var fixture = await Fixture.CreateAsync();
        var group = new UserGroupEntity
        {
            Name = "Music listeners", Permissions = (int)UserPermission.RequestMusic,
            MovieRequestLimit = null, TvRequestLimit = null, MusicRequestLimit = 4,
            MovieRequestLimitDays = 7, TvRequestLimitDays = 7, MusicRequestLimitDays = 14
        };
        fixture.Db.UserGroups.Add(group);
        var user = fixture.AddUser("listener", UserPermission.All, "User,Admin");
        user.UserGroup = group;
        user.DiscordUserId = "123456789012345678";
        await fixture.Db.SaveChangesAsync();

        var auth = new FixedAuth(user.UserId, "listener", adminClaim: true);
        var access = new UserAccessService(fixture.Db, auth, new ConfigurationBuilder().Build());
        var snapshot = await access.GetAccessAsync(user.UserId);

        Assert.False(snapshot.IsAdmin);
        Assert.False(await access.CanRequestAsync(user.UserId, MediaType.Movie));
        Assert.True(await access.CanRequestAsync(user.UserId, MediaType.Music));
        Assert.Equal(14, snapshot.MusicRequestLimitDays);

        var discord = new DiscordLinkService(fixture.Db, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DiscordLinkService>.Instance, access);
        Assert.False(await discord.IsAdminAsync(user.DiscordUserId));
    }

    [Fact]
    public async Task Rolling_quota_expires_completed_requests_and_ignores_rejected_or_cancelled()
    {
        await using var fixture = await Fixture.CreateAsync();
        var profile = fixture.AddUser("member", UserPermission.AllRequests, "User");
        profile.MovieRequestLimit = 1;
        profile.MovieRequestLimitDays = 7;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.MediaRequests.AddRange(
            Request(profile.UserId, RequestStatus.Available, DateTime.UtcNow.AddDays(-8)),
            Request(profile.UserId, RequestStatus.Cancelled, DateTime.UtcNow.AddHours(-1)),
            Request(profile.UserId, RequestStatus.Rejected, DateTime.UtcNow.AddHours(-1)));
        await fixture.Db.SaveChangesAsync();

        var auth = new FixedAuth(profile.UserId, "member");
        var access = new UserAccessService(fixture.Db, auth, new ConfigurationBuilder().Build());
        var service = RequestService(fixture.Db, auth, access);
        var quotas = new UserQuotaService(fixture.Db);
        Assert.True(await service.CheckRequestLimitsAsync(MediaType.Movie));

        fixture.Db.MediaRequests.Add(Request(profile.UserId, RequestStatus.Available, DateTime.UtcNow));
        await fixture.Db.SaveChangesAsync();
        Assert.False(await service.CheckRequestLimitsAsync(MediaType.Movie));
        var usage = await quotas.GetUsageAsync(profile.UserId, await access.GetAccessAsync(profile.UserId));
        Assert.Equal(1, usage.Movie.Used);
        Assert.Equal(0, usage.Movie.Remaining);
    }

    [Fact]
    public async Task Http_claims_are_rebuilt_from_current_database_access()
    {
        await using var fixture = await Fixture.CreateAsync();
        var profile = fixture.AddUser("live-access", UserPermission.AllRequests, "User");
        await fixture.Db.SaveChangesAsync();
        var auth = new FixedAuth(profile.UserId, "live-access", adminClaim: true);
        var access = new UserAccessService(fixture.Db, auth, new ConfigurationBuilder().Build());
        var services = new ServiceCollection().AddSingleton<IUserAccessService>(access).BuildServiceProvider();
        var transformer = new UserAccessClaimsTransformation(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<UserAccessClaimsTransformation>.Instance);
        ClaimsPrincipal Cookie(bool admin) => new(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "live-access"),
            new Claim("user_id", profile.UserId.ToString()),
            new Claim(ClaimTypes.Role, "User"),
            .. admin ? [new Claim(ClaimTypes.Role, "Admin")] : Array.Empty<Claim>()
        ], "test"));

        var staleCookie = Cookie(admin: true);
        staleCookie.AddIdentity(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")]));
        var demoted = await transformer.TransformAsync(staleCookie);
        Assert.False(demoted.IsInRole("Admin"));
        Assert.True(demoted.IsInRole("User"));

        profile.Permissions = (int)UserPermission.All;
        profile.Roles = "User,Admin";
        await fixture.Db.SaveChangesAsync();
        var promoted = await transformer.TransformAsync(Cookie(admin: false));
        Assert.True(promoted.IsInRole("Admin"));
    }

    [Fact]
    public async Task Suspending_an_account_revokes_web_discord_and_request_access()
    {
        await using var fixture = await Fixture.CreateAsync();
        var admin = fixture.AddUser("admin", UserPermission.All, "User,Admin");
        var member = fixture.AddUser("member", UserPermission.AllRequests, "User");
        member.DiscordUserId = "123456789012345678";
        member.DiscordDmOptIn = true;
        await fixture.Db.SaveChangesAsync();

        var configuration = new ConfigurationBuilder().Build();
        var adminAuth = new FixedAuth(admin.UserId, "admin", adminClaim: true);
        var adminAccess = new UserAccessService(fixture.Db, adminAuth, configuration);
        var users = new UserAdministrationService(fixture.Db, adminAccess, new UserQuotaService(fixture.Db), configuration);
        var suspendedUntil = DateTime.UtcNow.AddDays(7);
        var updated = await users.UpdateUserAccessAsync(Edit(member, UserAccountStatus.Suspended,
            suspendedUntil, "Repeated abuse"));
        Assert.True(updated.Success, updated.Message);

        var memberAuth = new FixedAuth(member.UserId, "member");
        var memberAccess = new UserAccessService(fixture.Db, memberAuth, configuration);
        var snapshot = await memberAccess.GetAccessAsync(member.UserId);
        Assert.False(snapshot.IsActive);
        Assert.Equal(UserAccountStatus.Suspended, snapshot.AccountStatus);
        Assert.Null(await memberAccess.GetCurrentUserIdAsync());
        Assert.False(await memberAccess.CanRequestAsync(member.UserId, MediaType.Movie));

        var links = new DiscordLinkService(fixture.Db, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DiscordLinkService>.Instance, memberAccess);
        var linkStatus = await links.GetStatusByDiscordIdAsync(member.DiscordUserId);
        Assert.True(linkStatus.Linked);
        Assert.False(linkStatus.AccountAvailable);
        Assert.False(linkStatus.DmOptIn);

        var request = await RequestService(fixture.Db, memberAuth, memberAccess)
            .RequestMediaForUserAsync(member.UserId, 42, MediaType.Movie);
        Assert.False(request.Success);
        Assert.Contains("suspended", request.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var services = new ServiceCollection().AddSingleton<IUserAccessService>(memberAccess).BuildServiceProvider();
        var transformer = new UserAccessClaimsTransformation(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<UserAccessClaimsTransformation>.Instance);
        var cookie = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "member"), new Claim("user_id", member.UserId.ToString()),
            new Claim(ClaimTypes.Role, "User")], "test"));
        cookie.AddIdentity(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "stale-secondary"),
            new Claim(ClaimTypes.Role, "Admin")], "secondary-test"));
        var revoked = await transformer.TransformAsync(cookie);
        Assert.False(revoked.Identity?.IsAuthenticated);
        Assert.DoesNotContain(revoked.Identities, identity => identity.IsAuthenticated);
        Assert.False(revoked.IsInRole("Admin"));
        Assert.Equal("suspended", revoked.FindFirst(UserAccessClaimsTransformation.AccountStatusClaim)?.Value);
    }

    [Fact]
    public async Task Expired_suspension_restores_access_without_admin_intervention()
    {
        await using var fixture = await Fixture.CreateAsync();
        var member = fixture.AddUser("member", UserPermission.AllRequests, "User");
        member.AccountStatus = UserAccountStatus.Suspended;
        member.SuspendedUntil = DateTime.UtcNow.AddMinutes(-1);
        member.AccountStatusReason = "Temporary hold";
        await fixture.Db.SaveChangesAsync();
        var access = new UserAccessService(fixture.Db, new FixedAuth(member.UserId, "member"),
            new ConfigurationBuilder().Build());

        var snapshot = await access.GetAccessAsync(member.UserId);

        Assert.True(snapshot.IsActive);
        Assert.True(await access.CanRequestAsync(member.UserId, MediaType.Movie));
        Assert.Equal(member.UserId, await access.GetCurrentUserIdAsync());
    }

    [Fact]
    public async Task Profile_reads_the_authenticated_user_not_the_last_login()
    {
        await using var fixture = await Fixture.CreateAsync();
        var current = fixture.AddUser("current", UserPermission.AllRequests, "User", DateTime.UtcNow.AddDays(-2));
        fixture.AddUser("someone-else", UserPermission.AllRequests, "User", DateTime.UtcNow);
        await fixture.Db.SaveChangesAsync();
        var auth = new FixedAuth(current.UserId, "current");
        var access = new UserAccessService(fixture.Db, auth, new ConfigurationBuilder().Build());

        var profile = await new UserProfileService(fixture.Db, access).GetProfileAsync();

        Assert.NotNull(profile);
        Assert.Equal("current", profile!.Username);
        Assert.Equal(current.UserId, profile.Id);
    }

    [Fact]
    public async Task Last_administrator_cannot_be_demoted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var admin = fixture.AddUser("admin", UserPermission.All, "User,Admin");
        await fixture.Db.SaveChangesAsync();
        var auth = new FixedAuth(admin.UserId, "admin", adminClaim: true);
        var configuration = new ConfigurationBuilder().Build();
        var access = new UserAccessService(fixture.Db, auth, configuration);
        var users = new UserAdministrationService(fixture.Db, access, new UserQuotaService(fixture.Db), configuration);

        var result = await users.UpdateUserAccessAsync(new UserAccessEditDto
        {
            UserId = admin.UserId,
            CustomPermissions = UserPermission.AllRequests,
            MovieRequestLimit = 5, MovieRequestLimitDays = 7,
            TvRequestLimit = 3, TvRequestLimitDays = 7,
            MusicRequestLimit = 10, MusicRequestLimitDays = 7
        });

        Assert.False(result.Success);
        Assert.Contains("administrator", result.Message, StringComparison.OrdinalIgnoreCase);

        var suspended = await users.UpdateUserAccessAsync(Edit(admin, UserAccountStatus.Suspended,
            DateTime.UtcNow.AddDays(1), "Security review"));
        Assert.False(suspended.Success);
        Assert.Contains("administrator", suspended.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Configured_admin_remains_an_emergency_admin_even_in_a_browse_only_group()
    {
        await using var fixture = await Fixture.CreateAsync();
        var guests = new UserGroupEntity
        {
            Name = "Guests", Permissions = (int)UserPermission.None,
            MovieRequestLimitDays = 7, TvRequestLimitDays = 7, MusicRequestLimitDays = 7
        };
        fixture.Db.UserGroups.Add(guests);
        var profile = fixture.AddUser("break-glass", UserPermission.None, "User");
        profile.UserGroup = guests;
        await fixture.Db.SaveChangesAsync();
        var auth = new FixedAuth(profile.UserId, "break-glass");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Admin:Usernames"] = "break-glass"
        }).Build();
        var access = new UserAccessService(fixture.Db, auth, configuration);

        var snapshot = await access.GetAccessAsync(profile.UserId);

        Assert.True(snapshot.IsAdmin);
        Assert.True(await access.CanRequestAsync(profile.UserId, MediaType.Movie));
        Assert.Null(snapshot.MovieRequestLimit);

        var users = new UserAdministrationService(fixture.Db, access, new UserQuotaService(fixture.Db), configuration);
        var disabled = await users.UpdateUserAccessAsync(Edit(profile, UserAccountStatus.Disabled,
            null, "Compromised account"));
        Assert.False(disabled.Success);
        Assert.Contains("emergency", disabled.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Built_in_groups_self_heal_without_overwriting_admin_edits()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeder = new UserGroupSeeder(fixture.Db);
        await seeder.SeedAsync();
        var members = await fixture.Db.UserGroups.SingleAsync(x => x.Name == "Members");
        members.MovieRequestLimit = 12;
        var guests = await fixture.Db.UserGroups.SingleAsync(x => x.Name == "Guests");
        fixture.Db.UserGroups.Remove(guests);
        await fixture.Db.SaveChangesAsync();

        await seeder.SeedAsync();
        await seeder.SeedAsync();

        Assert.Equal(4, await fixture.Db.UserGroups.CountAsync());
        Assert.Equal(12, await fixture.Db.UserGroups.Where(x => x.Name == "Members")
            .Select(x => x.MovieRequestLimit).SingleAsync());
        Assert.True(await fixture.Db.UserGroups.Where(x => x.Name == "Guests").Select(x => x.IsSystem).SingleAsync());
    }

    private static MediaRequestService RequestService(AppDbContext db, AuthenticationStateProvider auth, IUserAccessService access) =>
        new(db, auth, null!, null!, null!, new ConfigurationBuilder().Build(), null!, null!, null!,
            null!, null!, null!, null!, access, new UserQuotaService(db));

    private static MediaRequestEntity Request(int userId, RequestStatus status, DateTime requestedAt) => new()
    {
        MediaId = Random.Shared.Next(1, int.MaxValue), MediaType = MediaType.Movie,
        Title = "Quota test", RequestedBy = "member", RequestedByUserId = userId,
        Status = status, RequestedAt = requestedAt
    };

    private static UserAccessEditDto Edit(UserProfileEntity profile, UserAccountStatus status,
        DateTime? suspendedUntil, string? reason) => new()
    {
        UserId = profile.UserId,
        CustomPermissions = (UserPermission)profile.Permissions,
        MovieRequestLimit = profile.MovieRequestLimit,
        MovieRequestLimitDays = profile.MovieRequestLimitDays,
        TvRequestLimit = profile.TvRequestLimit,
        TvRequestLimitDays = profile.TvRequestLimitDays,
        MusicRequestLimit = profile.MusicRequestLimit,
        MusicRequestLimitDays = profile.MusicRequestLimitDays,
        AccountStatus = status,
        SuspendedUntil = suspendedUntil,
        AccountStatusReason = reason
    };

    private sealed class FixedAuth(int userId, string username, bool adminClaim = false) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var claims = new List<Claim> { new(ClaimTypes.Name, username), new("user_id", userId.ToString()) };
            if (adminClaim) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))));
        }
    }

    private sealed class Fixture(SqliteConnection connection, AppDbContext db) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public UserProfileEntity AddUser(string username, UserPermission permissions, string roles, DateTime? lastLogin = null)
        {
            var user = new UserEntity { Username = username, DisplayName = username };
            Db.Users.Add(user);
            var profile = new UserProfileEntity
            {
                User = user, Permissions = (int)permissions, Roles = roles, AutoApprove = permissions.HasFlag(UserPermission.AutoApprove),
                LastLoginAt = lastLogin ?? DateTime.UtcNow, PlexUsername = username
            };
            Db.UserProfiles.Add(profile);
            return profile;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
