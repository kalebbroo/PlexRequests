using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequests.Downloader.Indexers;
using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class AnimeIndexerRoutingTests
{
    [Fact]
    public void Anime_classification_selects_anime_capabilities_without_changing_library_shape()
    {
        var indexer = new IndexerConfigDto
        {
            MediaCapabilities =
            [
                new() { MediaType = MediaType.Movie, Enabled = false, CategoriesCsv = "2000" },
                new() { MediaType = MediaType.TvShow, Enabled = false, CategoriesCsv = "5000" },
                new() { MediaType = MediaType.Anime, Enabled = true, CategoriesCsv = "5070,5075" }
            ]
        };
        var animeMovie = new FulfillmentJobDto
            { MediaType = MediaType.Movie, IsAnime = true, Title = "Anime Movie" };
        var ordinaryMovie = new FulfillmentJobDto
            { MediaType = MediaType.Movie, IsAnime = false, Title = "Movie" };

        Assert.Equal(MediaType.Movie, animeMovie.MediaType);
        Assert.Equal(MediaType.Anime, IndexerConfigDto.SearchMediaType(animeMovie));
        Assert.True(indexer.Supports(animeMovie));
        Assert.Equal("5070,5075", indexer.CategoriesFor(animeMovie));
        Assert.False(indexer.Supports(ordinaryMovie));
    }

    [Fact]
    public void Site_specific_and_torznab_queries_use_anime_categories_for_movie_and_tv_shapes()
    {
        var animeSeries = new FulfillmentJobDto
            { MediaType = MediaType.TvShow, IsAnime = true, Title = "Anime Series" };
        Assert.Equal("Anime", X1337xIndexerProvider.CategoryFor(animeSeries));
        Assert.Equal("Anime", X1337xIndexerProvider.CategoryFor(new FulfillmentJobDto
            { MediaType = MediaType.Anime, Title = "Legacy Anime" }));

        var config = new IndexerConfigDto
        {
            Url = "https://indexer.test/api", ApiKey = "secret",
            MediaCapabilities =
            [
                new() { MediaType = MediaType.Movie, Enabled = true, CategoriesCsv = "2000" },
                new() { MediaType = MediaType.Anime, Enabled = true, CategoriesCsv = "5070" }
            ]
        };
        var urls = TorznabIndexerProvider.BuildQueryUrls(config, new FulfillmentJobDto
        {
            MediaType = MediaType.Movie, IsAnime = true, Title = "Akira", Year = 1988,
            ImdbId = "tt0094625"
        }).ToList();

        var url = Assert.Single(urls);
        Assert.Contains("t=search", url);
        Assert.Contains("cat=5070", url);
        Assert.Contains("q=Akira", url);
        Assert.DoesNotContain("t=movie", url);
        Assert.DoesNotContain("imdbid=", url);
    }

    [Fact]
    public async Task Linked_interactive_search_carries_durable_anime_classification_to_worker_job()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var request = new MediaRequestEntity
        {
            MediaId = 42, MediaType = MediaType.TvShow, RequestScopeKind = RequestScopeKind.Series,
            Title = "Classified Anime", IsAnime = true, Status = RequestStatus.Available
        };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();
        db.SearchTasks.Add(new SearchTaskEntity
        {
            MediaRequestId = request.Id, MediaType = request.MediaType, MediaId = request.MediaId,
            MediaKind = MediaKind.Series, RequestScopeKind = RequestScopeKind.Series,
            Title = request.Title, ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        });
        await db.SaveChangesAsync();

        var service = new InteractiveSearchService(db, null!,
            NullLogger<InteractiveSearchService>.Instance);
        var claimed = Assert.IsType<SearchTaskDto>(await service.ClaimAsync("anime-test"));
        var workerJob = InteractiveSearchWorker.ToJob(claimed);

        Assert.Equal(MediaType.TvShow, workerJob.MediaType);
        Assert.True(workerJob.IsAnime);
        Assert.Equal(MediaType.Anime, IndexerConfigDto.SearchMediaType(workerJob));
    }
}
