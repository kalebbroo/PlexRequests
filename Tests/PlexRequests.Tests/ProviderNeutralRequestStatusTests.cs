using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;
using Xunit;

namespace PlexRequests.Tests;

public sealed class ProviderNeutralRequestStatusTests
{
    [Fact]
    public async Task Music_status_overlay_uses_canonical_string_identity_and_best_lifecycle_state()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var identities = new MediaIdentityService(db);
        var primaryRef = MediaRef.FromExternal("musicbrainz", "release-group", MediaType.Music, MediaKind.Album);
        var albumRef = MediaRef.FromExternal("youtube", "MPRE-album", MediaType.Music, MediaKind.Album);
        var identity = await identities.ResolveAsync(primaryRef);
        await identities.AddAliasAsync(identity.Id, albumRef);
        var movieRef = MediaRef.FromTmdb(42, MediaType.Movie);
        var movieIdentity = await identities.ResolveAsync(movieRef);
        db.MediaRequests.AddRange(
            Request(identity.Id, RequestStatus.Pending),
            Request(identity.Id, RequestStatus.Processing),
            new MediaRequestEntity
            {
                MediaIdentityId = movieIdentity.Id,
                MediaId = 42,
                MediaType = MediaType.Movie,
                RequestScopeKind = RequestScopeKind.Title,
                Title = "Movie",
                RequestedBy = "listener",
                Status = RequestStatus.Approved
            });
        await db.SaveChangesAsync();

        var service = new MediaRequestService(
            db,
            new FixedAuthenticationStateProvider("listener"),
            null!, null!, null!, new ConfigurationBuilder().Build(), null!, null!, null!,
            identities, null!, null!, null!);

        var statuses = await service.GetMyRequestStatusesAsync([
            albumRef,
            movieRef,
            MediaRef.FromExternal("youtube", "other-album", MediaType.Music, MediaKind.Album)
        ]);

        Assert.Equal(2, statuses.Count);
        Assert.Equal(RequestStatus.Processing, statuses[albumRef.StableKey]);
        Assert.Equal(RequestStatus.Approved, statuses[movieRef.StableKey]);
    }

    private static MediaRequestEntity Request(int identityId, RequestStatus status) => new()
    {
        MediaIdentityId = identityId,
        MediaId = 0,
        MediaType = MediaType.Music,
        RequestScopeKind = RequestScopeKind.Album,
        ExternalSource = "youtube",
        ExternalId = "MPRE-album",
        Title = "Album",
        RequestedBy = "listener",
        Status = status
    };

    private sealed class FixedAuthenticationStateProvider(string username) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, username)], "test"))));
    }
}
