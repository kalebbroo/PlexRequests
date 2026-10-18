using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class PlexLibraryIndexTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private AppDbContext _db = null!;
    private MemoryCache _cache = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        await _db.Database.EnsureCreatedAsync();
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _cache.Dispose();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task A_movie_is_in_the_library_when_its_tmdb_id_is_mapped()
    {
        _db.PlexMappings.Add(Mapping("tmdb:1226578", "rk-1", "Spider-Man: Brand New Day", 2026));
        await _db.SaveChangesAsync();

        Assert.True(await Service().IsMovieInLibraryAsync(1226578));
    }

    [Fact]
    public async Task A_title_and_year_match_alone_does_not_count_as_in_the_library()
    {
        _db.PlexMappings.Add(Mapping("guid:unrelated", "rk-2", "Spider-Man: Brand New Day", 2026));
        await _db.SaveChangesAsync();

        Assert.False(await Service().IsMovieInLibraryAsync(1226578));
    }

    private PlexApiService Service() => new(
        null!,
        new HttpClient(),
        Options.Create(new PlexConfiguration { PrimaryServerUrl = "http://plex.test:32400", ServerToken = "test-token" }),
        _cache,
        NullLogger<PlexApiService>.Instance,
        _db,
        null!);

    private static PlexMappingEntity Mapping(string externalKey, string ratingKey, string title, int year) => new()
    {
        ExternalKey = externalKey,
        RatingKey = ratingKey,
        Title = title,
        Year = year,
        MediaType = MediaType.Movie
    };
}
