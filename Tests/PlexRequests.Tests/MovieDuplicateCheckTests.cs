using System.Reflection;
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
using Xunit;

namespace PlexRequests.Tests;

public sealed class MovieDuplicateCheckTests
{
    private const int BrandNewDayTmdbId = 1226578;

    [Fact]
    public async Task A_stored_available_row_does_not_block_a_movie_that_plex_does_not_have()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.MediaRequests.Add(StoredRequest(RequestStatus.Available));
        await db.SaveChangesAsync();
        var service = Service(db, DispatchProxy.Create<IMediaMetadataProvider, MetadataFake>(), inLibrary: false);

        var (blocked, message) = await service.CheckScopeConflictAsync(
            userId: 2, mediaId: BrandNewDayTmdbId, MediaType.Movie, allSeasons: true, seasons: null, episodes: null);

        Assert.False(blocked, message);
    }

    [Fact]
    public async Task A_movie_that_plex_has_blocks_a_new_request()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var service = Service(db, DispatchProxy.Create<IMediaMetadataProvider, MetadataFake>(), inLibrary: true);

        var (blocked, message) = await service.CheckScopeConflictAsync(
            userId: 2, mediaId: BrandNewDayTmdbId, MediaType.Movie, allSeasons: true, seasons: null, episodes: null);

        Assert.True(blocked);
        Assert.Equal("This title is already available.", message);
    }

    private static MediaRequestService Service(AppDbContext db, IMediaMetadataProvider metadata, bool inLibrary)
    {
        var plex = DispatchProxy.Create<IPlexApiService, PlexAvailabilityFake>();
        ((PlexAvailabilityFake)(object)plex).InLibrary = inLibrary;
        return new MediaRequestService(
            db,
            new FixedAuthenticationStateProvider("listener"),
            metadata,
            null!, null!, new ConfigurationBuilder().Build(), null!, null!, null!, null!, null!, null!, null!, null!,
            new UserQuotaService(db),
            plex);
    }

    private static MediaRequestEntity StoredRequest(RequestStatus status) => new()
    {
        MediaId = BrandNewDayTmdbId,
        MediaType = MediaType.Movie,
        RequestScopeKind = RequestScopeKind.Title,
        ExternalSource = null,
        ExternalId = null,
        Title = "Spider-Man: Brand New Day",
        RequestedBy = "someone",
        Status = status
    };

    // Plex is the only dependency the movie check uses. The proxy answers the one call it needs; any other
    // member fails loudly, so a new dependency shows up as a test failure rather than a silent pass.
    public class PlexAvailabilityFake : DispatchProxy
    {
        public bool InLibrary { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IPlexApiService.AnnotateAvailabilityAsync))
            {
                foreach (var card in (List<MediaCardDto>)args![0]!) card.IsAvailable = InLibrary;
                return Task.CompletedTask;
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    public class MetadataFake : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IMediaMetadataProvider.GetDetailsAsync) && args?.Length == 2)
            {
                return Task.FromResult<MediaDetailDto?>(new MediaDetailDto
                {
                    Id = BrandNewDayTmdbId,
                    TmdbId = BrandNewDayTmdbId,
                    MediaType = MediaType.Movie,
                    Title = "Spider-Man: Brand New Day",
                    Year = 2026
                });
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class FixedAuthenticationStateProvider(string username) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, username) }, "test"))));
    }
}
