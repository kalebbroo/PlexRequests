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

public sealed class AnimeEpisodeOrderPersistenceTests
{
    [Fact]
    public async Task ClaimOffersOfficialEpisodeGroupsOnlyToUnmappedAnimeWithExactTargets()
    {
        await using var fixture = await Fixture.CreateAsync();
        var mapped = Profile("configured");
        var aired = Profile("aired");
        aired.SourceOrder = EpisodeOrderType.Aired;
        aired.MappingsText = string.Empty;
        fixture.Db.MediaRequests.AddRange(Request("Unmapped"), Request("Mapped"), Request("Aired"), Request("Ordinary TV"));
        await fixture.Db.SaveChangesAsync();
        var requests = await fixture.Db.MediaRequests.OrderBy(request => request.Id).ToListAsync();
        fixture.Db.FulfillmentJobs.AddRange(
            Job(requests[0], anime: true, episodeOrderJson: null),
            Job(requests[1], anime: true, JsonSerializer.Serialize(mapped)),
            Job(requests[2], anime: true, JsonSerializer.Serialize(aired)),
            Job(requests[3], anime: false, episodeOrderJson: null));
        await fixture.Db.SaveChangesAsync();
        var groups = new FakeEpisodeGroups(Profile("candidate"));
        var queue = fixture.Queue(new FixedPreferences(), groups);

        var claimed = await queue.ClaimNextAsync("worker", 4);

        Assert.Single(claimed[0].EpisodeOrderCandidates);
        Assert.Empty(claimed[1].EpisodeOrderCandidates);
        Assert.Empty(claimed[2].EpisodeOrderCandidates);
        Assert.Empty(claimed[3].EpisodeOrderCandidates);
        Assert.Equal(1, groups.CandidateCalls);
    }

    [Fact]
    public async Task SelectedGroupIsReimportedThenFrozenOnCurrentJobOnly()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = Request("Anime");
        fixture.Db.MediaRequests.Add(request);
        await fixture.Db.SaveChangesAsync();
        var job = Job(request, anime: true, episodeOrderJson: null);
        job.Status = FulfillmentStatus.Claimed;
        fixture.Db.FulfillmentJobs.Add(job);
        await fixture.Db.SaveChangesAsync();
        var authoritative = Profile("verified-group");
        var groups = new FakeEpisodeGroups(authoritative);
        var preferences = new FixedPreferences(fixture.Db);
        var queue = fixture.Queue(preferences, groups);

        var selected = await queue.ApplyEpisodeOrderGroupAsync(job.Id, "verified-group");

        Assert.NotNull(selected);
        Assert.Equal("verified-group", selected.SourceEpisodeGroupId);
        var persistedJob = await fixture.Db.FulfillmentJobs.AsNoTracking().SingleAsync();
        var persistedProfile = JsonSerializer.Deserialize<SeriesEpisodeOrderProfileDto>(persistedJob.EpisodeOrderProfileJson!);
        Assert.Equal(authoritative.MappingsText, persistedProfile!.MappingsText);
        var settings = await preferences.GetAsync();
        Assert.Empty(settings.SeriesEpisodeOrderProfiles);
        Assert.Equal(1, groups.PreviewCalls);
    }

    [Fact]
    public async Task SelectedGroupCannotBeAppliedToNonAnimeOrTerminalJobs()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.MediaRequests.AddRange(Request("TV"), Request("Finished Anime"));
        await fixture.Db.SaveChangesAsync();
        var requests = await fixture.Db.MediaRequests.OrderBy(request => request.Id).ToListAsync();
        var tv = Job(requests[0], anime: false, episodeOrderJson: null);
        var finished = Job(requests[1], anime: true, episodeOrderJson: null);
        finished.Status = FulfillmentStatus.Completed;
        fixture.Db.FulfillmentJobs.AddRange(tv, finished);
        await fixture.Db.SaveChangesAsync();
        var groups = new FakeEpisodeGroups(Profile("verified-group"));
        var queue = fixture.Queue(new FixedPreferences(fixture.Db), groups);

        Assert.Null(await queue.ApplyEpisodeOrderGroupAsync(tv.Id, "verified-group"));
        Assert.Null(await queue.ApplyEpisodeOrderGroupAsync(finished.Id, "verified-group"));
        Assert.Equal(0, groups.PreviewCalls);
    }

    [Fact]
    public async Task SelectionIsIdempotentButCannotOverwriteAFrozenDifferentOrder()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = Request("Anime");
        fixture.Db.MediaRequests.Add(request);
        await fixture.Db.SaveChangesAsync();
        var frozen = Profile("already-frozen");
        var job = Job(request, anime: true, JsonSerializer.Serialize(frozen));
        job.Status = FulfillmentStatus.Claimed;
        fixture.Db.FulfillmentJobs.Add(job);
        await fixture.Db.SaveChangesAsync();
        var groups = new FakeEpisodeGroups(Profile("different"));
        var queue = fixture.Queue(new FixedPreferences(fixture.Db), groups);

        var same = await queue.ApplyEpisodeOrderGroupAsync(job.Id, "already-frozen");
        var replacement = await queue.ApplyEpisodeOrderGroupAsync(job.Id, "different");

        Assert.Equal("already-frozen", same!.SourceEpisodeGroupId);
        Assert.Null(replacement);
        Assert.Equal(0, groups.PreviewCalls);
    }

    private static MediaRequestEntity Request(string title) => new()
    {
        MediaId = 46195,
        MediaType = MediaType.TvShow,
        RequestScopeKind = RequestScopeKind.Series,
        Title = title,
        Status = RequestStatus.Approved
    };

    private static FulfillmentJobEntity Job(MediaRequestEntity request, bool anime, string? episodeOrderJson) => new()
    {
        MediaRequestId = request.Id,
        MediaId = request.MediaId,
        TmdbId = request.MediaId,
        MediaType = MediaType.TvShow,
        MediaKind = MediaKind.Series,
        RequestScopeKind = RequestScopeKind.Series,
        Title = request.Title,
        Status = FulfillmentStatus.Queued,
        IsAnime = anime,
        EpisodeOrderProfileJson = episodeOrderJson,
        SeasonTargetsJson = JsonSerializer.Serialize(new List<SeasonTarget>
        {
            new() { Season = 1, EpisodeCount = 2, MissingEpisodes = [1, 2] }
        })
    };

    private static SeriesEpisodeOrderProfileDto Profile(string groupId) => new()
    {
        TmdbId = 46195,
        SeriesTitle = "Monogatari",
        SourceOrder = EpisodeOrderType.Custom,
        SourceEpisodeGroupId = groupId,
        SourceEpisodeGroupName = $"Group {groupId}",
        SourceGroups = [new EpisodeOrderSourceGroupDto { SourceSeason = 1, Name = "First arc" }],
        MappingsText = "S01E01 -> S01E01\nS01E02 -> S01E02",
        Enabled = true
    };

    private sealed class FakeEpisodeGroups(SeriesEpisodeOrderProfileDto profile)
        : ITmdbEpisodeGroupImportService
    {
        public int CandidateCalls { get; private set; }
        public int PreviewCalls { get; private set; }

        public Task<List<MediaCardDto>> SearchSeriesAsync(string query, CancellationToken ct = default) =>
            Task.FromResult(new List<MediaCardDto>());

        public Task<List<TmdbEpisodeGroupSummaryDto>> GetGroupsAsync(int tmdbId,
            CancellationToken ct = default) => Task.FromResult(new List<TmdbEpisodeGroupSummaryDto>());

        public Task<List<SeriesEpisodeOrderProfileDto>> GetCandidateProfilesAsync(int tmdbId,
            string seriesTitle, CancellationToken ct = default)
        {
            CandidateCalls++;
            return Task.FromResult(new List<SeriesEpisodeOrderProfileDto> { profile });
        }

        public Task<EpisodeGroupImportPreviewDto> BuildPreviewAsync(int tmdbId, string seriesTitle,
            string groupId, CancellationToken ct = default)
        {
            PreviewCalls++;
            if (!string.Equals(groupId, profile.SourceEpisodeGroupId, StringComparison.Ordinal))
                throw new ArgumentException("Unknown group");
            return Task.FromResult(new EpisodeGroupImportPreviewDto
            {
                Profile = profile,
                SourceEpisodeCount = 2,
                MappedEpisodeCount = 2
            });
        }
    }

    private sealed class FixedPreferences(AppDbContext? db = null) : ILibraryOrganizationPreferencesService
    {
        private LibraryOrganizationPreferencesDto _value = new();
        public Task<LibraryOrganizationPreferencesDto> GetAsync() => Task.FromResult(_value);
        public async Task<bool> UpdateAsync(LibraryOrganizationPreferencesDto prefs)
        {
            _value = prefs;
            if (db is not null) await db.SaveChangesAsync();
            return true;
        }
    }

    private sealed class Fixture(SqliteConnection connection, AppDbContext db) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public FulfillmentQueue Queue(ILibraryOrganizationPreferencesService preferences,
            ITmdbEpisodeGroupImportService groups) => new(Db, null!, null!, null!, null!, null!, preferences,
            NullLogger<FulfillmentQueue>.Instance, groups);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
