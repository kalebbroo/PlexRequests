using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;
using Xunit;

namespace PlexRequests.Tests;

public sealed class ReplacementContextTests
{
    [Fact]
    public async Task Legacy_anime_replacement_rehydrates_routing_language_and_episode_order_snapshots()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var request = new MediaRequestEntity
        {
            MediaId = 9001, MediaType = MediaType.TvShow, RequestScopeKind = RequestScopeKind.Series,
            Title = "Legacy Anime", Status = RequestStatus.Available, IsAnime = null,
            QualityProfileId = 9, LibraryDestinationId = "anime-tv"
        };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();
        db.FulfillmentJobs.Add(new FulfillmentJobEntity
        {
            MediaRequestId = request.Id, MediaId = request.MediaId, MediaType = request.MediaType,
            Title = request.Title, Status = FulfillmentStatus.Completed,
            // Simulate a row created before these context columns existed.
            MediaKind = MediaKind.Movie, IsAnime = false
        });
        await db.SaveChangesAsync();

        var metadata = new FixedMetadata(new MediaDetailDto
        {
            Id = request.MediaId, MediaType = MediaType.TvShow, Title = request.Title,
            Year = 2017, ImdbId = "tt1234567", TvdbId = 7654,
            Genres = ["Animation", "Action"], Languages = ["ja"], Countries = ["JP"]
        });
        var queue = Queue(db, metadata, Preferences());

        var jobId = await queue.EnqueueReplacementAsync(new MediaRequestDto
        {
            Id = request.Id, MediaId = request.MediaId, MediaType = request.MediaType,
            Title = request.Title
        }, Quality.FullHD, ["/library/legacy/bad.mkv"], [(2, 1)], mediaIssueId: 77);

        Assert.NotNull(jobId);
        var replacement = await db.FulfillmentJobs.SingleAsync(j => j.Id == jobId);
        Assert.True(replacement.IsAnime);
        Assert.Equal(MediaKind.Series, replacement.MediaKind);
        Assert.Equal(RequestScopeKind.Episodes, replacement.RequestScopeKind);
        Assert.Equal(2017, replacement.Year);
        Assert.Equal("tt1234567", replacement.ImdbId);
        Assert.Equal(7654, replacement.TvdbId);
        Assert.Equal(9, replacement.QualityProfileId);
        Assert.Equal("anime-tv", replacement.LibraryDestinationId);
        Assert.Equal("/library/anime-tv", replacement.LibraryDestinationRootPath);
        Assert.Equal(LibraryContentKind.Series, replacement.LibraryDestinationKind);
        Assert.Contains("{Season", replacement.LibraryDestinationTemplate);
        Assert.Equal(["Animation", "Action"], replacement.GenresCsv!.Split(','));
        var order = JsonSerializer.Deserialize<SeriesEpisodeOrderProfileDto>(replacement.EpisodeOrderProfileJson!);
        Assert.Equal(EpisodeOrderType.Absolute, order!.SourceOrder);
        Assert.Contains("A13 -> S02E01", order.MappingsText);
        var policy = JsonSerializer.Deserialize<MediaLanguagePolicyDto>(replacement.MediaLanguagePolicyJson!);
        Assert.Equal(["en", "ja"], policy!.RequiredAudioLanguages);
        Assert.Equal(["en"], policy.RequiredSubtitleLanguages);
        Assert.True((await db.MediaRequests.SingleAsync()).IsAnime);
    }

    [Fact]
    public async Task Replacement_preserves_an_existing_immutable_route_and_episode_order_snapshot()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var request = new MediaRequestEntity
        {
            MediaId = 55, MediaType = MediaType.TvShow, RequestScopeKind = RequestScopeKind.Series,
            Title = "Kids Show", Status = RequestStatus.Available, QualityProfileId = 9
        };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();
        var originalOrder = JsonSerializer.Serialize(new SeriesEpisodeOrderProfileDto
        {
            TmdbId = request.MediaId, Enabled = true, SourceOrder = EpisodeOrderType.Dvd,
            MappingsText = "S01E01 -> S01E02"
        });
        db.FulfillmentJobs.Add(new FulfillmentJobEntity
        {
            MediaRequestId = request.Id, MediaId = request.MediaId, MediaType = request.MediaType,
            MediaKind = MediaKind.Series, Title = request.Title, Status = FulfillmentStatus.Completed,
            QualityProfileId = 9, EpisodeOrderProfileJson = originalOrder,
            LibraryDestinationId = "old-kids", LibraryDestinationName = "Original Kids TV",
            LibraryDestinationKind = LibraryContentKind.Series,
            LibraryDestinationRootPath = "/library/original-kids",
            LibraryDestinationTemplate = "{Title}/Season {Season:00}/{Title} - S{Season:00}E{Episode:00}{Ext}"
        });
        await db.SaveChangesAsync();

        var queue = Queue(db, new FixedMetadata(null), Preferences());
        var jobId = await queue.EnqueueUpgradeAsync(new MediaRequestDto
        {
            Id = request.Id, MediaId = request.MediaId, MediaType = request.MediaType, Title = request.Title
        }, Quality.FullHD, ["/library/original-kids/bad.mkv"], [(1, 2)]);

        Assert.True(jobId);
        var replacement = await db.FulfillmentJobs.OrderByDescending(j => j.Id).FirstAsync();
        Assert.Equal("old-kids", replacement.LibraryDestinationId);
        Assert.Equal("/library/original-kids", replacement.LibraryDestinationRootPath);
        Assert.Equal(originalOrder, replacement.EpisodeOrderProfileJson);
    }

    private static FulfillmentQueue Queue(AppDbContext db, IMediaMetadataProvider metadata,
        LibraryOrganizationPreferencesDto preferences) => new(db, metadata, null!, new FixedProfiles(),
        null!, null!, new FixedPreferences(preferences), NullLogger<FulfillmentQueue>.Instance);

    private static LibraryOrganizationPreferencesDto Preferences() => new()
    {
        MoviePath = "/library/movies",
        TvPath = "/library/tv",
        MusicPath = "/library/music",
        TvEpisodeTemplate = "{Title}/Season {Season:00}/{Title} - S{Season:00}E{Episode:00}{Ext}",
        SeasonPackFolderTemplate = "{Title}/Season {Season:00}/{FileName}",
        LibraryDestinations =
        [
            new() { Id = "tv", Name = "TV", ContentKind = LibraryContentKind.Series,
                RootPath = "/library/tv", Enabled = true, IsDefault = true },
            new() { Id = "anime-tv", Name = "Anime TV", ContentKind = LibraryContentKind.Series,
                RootPath = "/library/anime-tv", Enabled = true }
        ],
        LibraryRootRules =
        [
            new() { MediaType = MediaType.TvShow, RequireAnime = true, DestinationId = "anime-tv" }
        ],
        SeriesEpisodeOrderProfiles =
        [
            new()
            {
                TmdbId = 9001, SeriesTitle = "Legacy Anime", Enabled = true,
                SourceOrder = EpisodeOrderType.Absolute, MappingsText = "A13 -> S02E01"
            }
        ]
    };

    private sealed class FixedPreferences(LibraryOrganizationPreferencesDto value)
        : ILibraryOrganizationPreferencesService
    {
        public Task<LibraryOrganizationPreferencesDto> GetAsync() => Task.FromResult(value);
        public Task<bool> UpdateAsync(LibraryOrganizationPreferencesDto prefs) => Task.FromResult(false);
    }

    private sealed class FixedMetadata(MediaDetailDto? detail) : IMediaMetadataProvider
    {
        public Task<List<MediaCardDto>> SearchAsync(string query, MediaType? mediaType = null,
            int page = 1, int pageSize = 20) => Task.FromResult(new List<MediaCardDto>());
        public Task<MediaDetailDto?> GetDetailsAsync(int mediaId, MediaType mediaType) =>
            Task.FromResult(detail);
        public Task<MediaDetailDto?> GetDetailsAsync(MediaRef mediaRef) => Task.FromResult(detail);
        public Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10) =>
            Task.FromResult(new List<MediaCardDto>());
        public Task<List<MediaCardDto>> GetLibraryAsync(MediaType mediaType, int page = 1,
            int pageSize = 20) => Task.FromResult(new List<MediaCardDto>());
        public Task<string?> GetImdbIdAsync(int mediaId, MediaType mediaType) => Task.FromResult<string?>(null);
    }

    private sealed class FixedProfiles : IQualityProfileService
    {
        public Task<int> ResolveProfileIdAsync(MediaType mediaType, int tmdbId, IEnumerable<string>? genres,
            int? requesterChoiceId, int? userId, string? library = null, bool? isAnime = null) =>
            Task.FromResult(9);
        public Task<Quality> GetCutoffQualityAsync(int profileId) => Task.FromResult(Quality.FullHD);
        public Task<QualityProfileDto?> GetProfileAsync(int id) => Task.FromResult<QualityProfileDto?>(new()
        {
            Id = id, Name = "Dual audio anime", RequiredAudioLanguagesCsv = "en,ja",
            RequiredSubtitleLanguagesCsv = "en", AllowUnknownTrackLanguage = false
        });
        public Task<List<QualityDefinitionDto>> GetDefinitionsAsync() => throw new NotSupportedException();
        public Task<List<QualityProfileDto>> GetProfilesAsync() => throw new NotSupportedException();
        public Task<List<QualityProfileSummaryDto>> GetSelectableProfilesAsync(int? userId, MediaType mediaType) => throw new NotSupportedException();
        public Task<QualityProfileDto> CreateProfileAsync(string name) => throw new NotSupportedException();
        public Task<bool> UpdateProfileAsync(QualityProfileDto profile) => throw new NotSupportedException();
        public Task<(bool ok, string? error)> DeleteProfileAsync(int id) => throw new NotSupportedException();
        public Task<bool> SetDefaultProfileAsync(int id) => throw new NotSupportedException();
        public Task<List<ProfileAssignmentRuleDto>> GetAssignmentRulesAsync() => throw new NotSupportedException();
        public Task<ProfileAssignmentRuleDto> CreateAssignmentRuleAsync() => throw new NotSupportedException();
        public Task<(bool ok, string? error)> UpdateAssignmentRuleAsync(ProfileAssignmentRuleDto rule) => throw new NotSupportedException();
        public Task<bool> DeleteAssignmentRuleAsync(int id) => throw new NotSupportedException();
        public Task<bool> MoveAssignmentRuleAsync(int id, bool up) => throw new NotSupportedException();
    }
}
