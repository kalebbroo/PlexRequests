using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class LegacyJobEpisodeOrderTests
{
    [Fact]
    public async Task Claim_snapshots_only_missing_tmdb_episode_order_context()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.MediaRequests.AddRange(
            Request(9001, "Legacy Anime"),
            Request(9001, "Already Frozen"),
            Request(9001, "External Series"));
        await db.SaveChangesAsync();
        var requests = await db.MediaRequests.OrderBy(x => x.Id).ToListAsync();
        var existing = JsonSerializer.Serialize(new SeriesEpisodeOrderProfileDto
        {
            TmdbId = 9001, SeriesTitle = "Old order", SourceOrder = EpisodeOrderType.Dvd,
            MappingsText = "S01E01 -> S01E01", Enabled = true
        });
        db.FulfillmentJobs.AddRange(
            Job(requests[0], "Legacy Anime"),
            Job(requests[1], "Already Frozen", existing),
            Job(requests[2], "External Series", externalId: "tvdb-9001"));
        await db.SaveChangesAsync();

        var preferences = new FixedPreferences(new LibraryOrganizationPreferencesDto
        {
            SeriesEpisodeOrderProfiles =
            [
                new()
                {
                    TmdbId = 9001, SeriesTitle = "Legacy Anime", SourceOrder = EpisodeOrderType.Absolute,
                    MappingsText = "A13 -> S02E01", Enabled = true
                }
            ]
        });
        var queue = new FulfillmentQueue(db, null!, null!, null!, null!, null!, preferences,
            NullLogger<FulfillmentQueue>.Instance);

        var claimed = await queue.ClaimNextAsync("worker", 3);

        Assert.Equal(3, claimed.Count);
        var hydrated = Assert.IsType<SeriesEpisodeOrderProfileDto>(claimed[0].EpisodeOrderProfile);
        Assert.Equal(EpisodeOrderType.Absolute, hydrated.SourceOrder);
        Assert.Contains("A13 -> S02E01", hydrated.MappingsText);
        var frozen = Assert.IsType<SeriesEpisodeOrderProfileDto>(claimed[1].EpisodeOrderProfile);
        Assert.Equal(EpisodeOrderType.Dvd, frozen.SourceOrder);
        Assert.Null(claimed[2].EpisodeOrderProfile);

        var persisted = await db.FulfillmentJobs.OrderBy(x => x.Id).ToListAsync();
        Assert.NotNull(persisted[0].EpisodeOrderProfileJson);
        Assert.Equal(existing, persisted[1].EpisodeOrderProfileJson);
        Assert.Null(persisted[2].EpisodeOrderProfileJson);
    }

    private static MediaRequestEntity Request(int mediaId, string title) => new()
    {
        MediaId = mediaId,
        MediaType = MediaType.TvShow,
        RequestScopeKind = RequestScopeKind.Series,
        Title = title,
        Status = RequestStatus.Approved
    };

    private static FulfillmentJobEntity Job(MediaRequestEntity request, string title,
        string? episodeOrderJson = null, string? externalId = null) => new()
    {
        MediaRequestId = request.Id,
        MediaId = request.MediaId,
        MediaType = MediaType.TvShow,
        MediaKind = MediaKind.Series,
        RequestScopeKind = RequestScopeKind.Series,
        Title = title,
        Status = FulfillmentStatus.Queued,
        EpisodeOrderProfileJson = episodeOrderJson,
        ExternalId = externalId,
        ExternalSource = externalId is null ? null : "tvdb"
    };

    private sealed class FixedPreferences(LibraryOrganizationPreferencesDto value)
        : ILibraryOrganizationPreferencesService
    {
        public Task<LibraryOrganizationPreferencesDto> GetAsync() => Task.FromResult(value);
        public Task<bool> UpdateAsync(LibraryOrganizationPreferencesDto prefs) => Task.FromResult(false);
    }
}
