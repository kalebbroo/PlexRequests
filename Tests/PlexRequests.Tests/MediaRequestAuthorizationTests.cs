using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;
using Xunit;

namespace PlexRequests.Tests;

public sealed class MediaRequestAuthorizationTests
{
    [Fact]
    public async Task Personal_request_views_stay_scoped_for_administrators_and_regular_users()
    {
        await using var fixture = await Fixture.CreateAsync();
        var admin = fixture.AddUser("admin", UserPermission.All);
        var member = fixture.AddUser("member", UserPermission.AllRequests);
        await fixture.Db.SaveChangesAsync();
        var identities = new MediaIdentityService(fixture.Db);
        var adminRef = MediaRef.FromTmdb(101, MediaType.Movie);
        var memberRef = MediaRef.FromTmdb(202, MediaType.Movie);
        var adminIdentity = await identities.ResolveAsync(adminRef);
        var memberIdentity = await identities.ResolveAsync(memberRef);
        var adminRequest = Request(admin.UserId, adminIdentity.Id, 101, "member", RequestStatus.Pending);
        var memberRequest = Request(member.UserId, memberIdentity.Id, 202, "member", RequestStatus.Pending);
        var memberAvailable = Request(member.UserId, memberIdentity.Id, 203, "member", RequestStatus.Available);
        fixture.Db.MediaRequests.AddRange(adminRequest, memberRequest, memberAvailable);
        fixture.Db.Watchlist.AddRange(
            new WatchlistItemEntity { UserId = admin.UserId, Username = "member", MediaId = 301, MediaType = MediaType.Movie },
            new WatchlistItemEntity { UserId = member.UserId, Username = "member", MediaId = 302, MediaType = MediaType.Movie });
        await fixture.Db.SaveChangesAsync();

        var adminService = fixture.Service(admin.UserId, "admin", identities);
        var adminStats = await adminService.GetMyStatsAsync();
        var adminPage = await adminService.GetRequestsAsync(new MediaFilterDto { PageNumber = 1, PageSize = 20 });
        var approvalQueue = await adminService.GetAdminRequestsAsync(new MediaFilterDto
        {
            RequestStatus = RequestStatus.Pending,
            PageNumber = 1,
            PageSize = 20
        });
        var adminStatuses = await adminService.GetMyRequestStatusesAsync([adminRef, memberRef]);

        Assert.Equal(1, adminStats.TotalRequests);
        Assert.Equal(adminRequest.Id, Assert.Single(adminPage.Items).Id);
        Assert.Equal(2, approvalQueue.TotalCount);
        Assert.Contains(approvalQueue.Items, x => x.Id == adminRequest.Id);
        Assert.Contains(approvalQueue.Items, x => x.Id == memberRequest.Id);
        Assert.Equal(RequestStatus.Pending, Assert.Single(adminStatuses).Value);
        Assert.NotNull(await adminService.GetRequestByIdAsync(memberRequest.Id));
        Assert.True(await adminService.IsInWatchlistAsync(301, MediaType.Movie));
        Assert.False(await adminService.IsInWatchlistAsync(302, MediaType.Movie));

        var memberService = fixture.Service(member.UserId, "member", identities);
        Assert.Empty((await memberService.GetAdminRequestsAsync(
            new MediaFilterDto { PageNumber = 1, PageSize = 20 })).Items);
        Assert.Null(await memberService.GetRequestByIdAsync(adminRequest.Id));
        Assert.False(await memberService.CancelRequestAsync(adminRequest.Id));
        Assert.False(await memberService.CancelRequestAsync(memberAvailable.Id));
        Assert.False(await memberService.RemoveFromWatchlistAsync(301, MediaType.Movie));
        Assert.True(await memberService.IsInWatchlistAsync(302, MediaType.Movie));
        Assert.Equal(RequestStatus.Pending,
            await fixture.Db.MediaRequests.Where(x => x.Id == adminRequest.Id).Select(x => x.Status).SingleAsync());
    }

    [Fact]
    public async Task Manual_episode_upgrade_cannot_bypass_tv_request_permission()
    {
        await using var fixture = await Fixture.CreateAsync();
        var member = fixture.AddUser("movies-only", UserPermission.RequestMovies);
        await fixture.Db.SaveChangesAsync();
        var service = fixture.Service(member.UserId, "movies-only", new MediaIdentityService(fixture.Db));

        var result = await service.RequestEpisodesAsync(
            42, MediaType.TvShow, [(9, 1)], qualityProfileId: null, asUpgrade: true);

        Assert.False(result.Success);
        Assert.Contains("not allowed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Db.FulfillmentJobs);
    }

    private static MediaRequestEntity Request(int userId, int identityId, int mediaId, string username,
        RequestStatus status) => new()
    {
        MediaIdentityId = identityId,
        MediaId = mediaId,
        MediaType = MediaType.Movie,
        RequestScopeKind = RequestScopeKind.Title,
        Title = $"Movie {mediaId}",
        PosterUrl = "/img/poster-placeholder.svg",
        RequestedByUserId = userId,
        RequestedBy = username,
        Status = status,
        RequestedAt = DateTime.UtcNow
    };

    private sealed class FixedAuth(int userId, string username) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.Name, username), new Claim("user_id", userId.ToString())
            ], "test"))));
    }

    private sealed class Fixture(SqliteConnection connection, AppDbContext db) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        private readonly IConfiguration _configuration = new ConfigurationBuilder().Build();

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public UserProfileEntity AddUser(string username, UserPermission permissions)
        {
            var user = new UserEntity { Username = username, DisplayName = username };
            Db.Users.Add(user);
            var profile = new UserProfileEntity
            {
                User = user,
                Permissions = (int)permissions,
                Roles = permissions.HasFlag(UserPermission.Administrator) ? "User,Admin" : "User"
            };
            Db.UserProfiles.Add(profile);
            return profile;
        }

        public MediaRequestService Service(int userId, string username, IMediaIdentityService identities)
        {
            var auth = new FixedAuth(userId, username);
            var access = new UserAccessService(Db, auth, _configuration);
            return new MediaRequestService(Db, auth, null!, null!, null!, _configuration, null!, null!, null!,
                identities, null!, null!, null!, access, new UserQuotaService(Db));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
