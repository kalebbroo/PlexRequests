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

public sealed class LegacyMediaLanguagePolicyTests
{
    [Fact]
    public async Task ClaimSnapshotsMissingPolicyButPreservesExistingContract()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var profile = new QualityProfileEntity
        {
            Name = "Anime safe",
            IsDefault = true,
            LanguagePreference = ReleaseLanguagePreference.Smart,
            PreferredAudioLanguage = "en",
            PreferredSubtitleLanguage = "en",
            SetPreferredTracksAsDefault = true
        };
        db.QualityProfiles.Add(profile);
        db.MediaRequests.AddRange(Request("Legacy", profile), Request("Frozen", profile));
        await db.SaveChangesAsync();
        var requests = await db.MediaRequests.OrderBy(request => request.Id).ToListAsync();
        var frozen = JsonSerializer.Serialize(new MediaLanguagePolicyDto
        {
            Preference = ReleaseLanguagePreference.EnglishOnly,
            PreferredAudioLanguage = "fr"
        });
        db.FulfillmentJobs.AddRange(Job(requests[0], profile.Id), Job(requests[1], profile.Id, frozen));
        await db.SaveChangesAsync();

        var profiles = new QualityProfileService(db, NullLogger<QualityProfileService>.Instance);
        var formats = new CustomFormatService(db, NullLogger<CustomFormatService>.Instance);
        var queue = new FulfillmentQueue(db, null!, null!, profiles, formats, null!,
            new FixedPreferences(), NullLogger<FulfillmentQueue>.Instance);

        var claimed = await queue.ClaimNextAsync("worker", 2);

        Assert.Equal(2, claimed.Count);
        var hydratedPolicy = Assert.IsType<MediaLanguagePolicyDto>(claimed[0].MediaLanguagePolicy);
        Assert.Equal(ReleaseLanguagePreference.Smart, hydratedPolicy.Preference);
        Assert.Equal("en", hydratedPolicy.PreferredAudioLanguage);
        var frozenPolicy = Assert.IsType<MediaLanguagePolicyDto>(claimed[1].MediaLanguagePolicy);
        Assert.Equal(ReleaseLanguagePreference.EnglishOnly, frozenPolicy.Preference);
        Assert.Equal("fr", frozenPolicy.PreferredAudioLanguage);
        var persisted = await db.FulfillmentJobs.OrderBy(job => job.Id).ToListAsync();
        Assert.NotNull(persisted[0].MediaLanguagePolicyJson);
        Assert.Equal(frozen, persisted[1].MediaLanguagePolicyJson);
    }

    [Fact]
    public async Task ClaimUsesSafeSmartFallbackWhenLegacyProfileCannotBeResolved()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var request = Request("Orphaned profile", null);
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();
        db.FulfillmentJobs.Add(Job(request, null));
        await db.SaveChangesAsync();
        var queue = new FulfillmentQueue(db, null!, null!,
            new QualityProfileService(db, NullLogger<QualityProfileService>.Instance), null!, null!,
            new FixedPreferences(), NullLogger<FulfillmentQueue>.Instance);

        var claimed = Assert.Single(await queue.ClaimNextAsync("worker"));

        Assert.Equal(ReleaseLanguagePreference.Smart, claimed.MediaLanguagePolicy!.Preference);
        Assert.Equal("en", claimed.MediaLanguagePolicy.PreferredAudioLanguage);
        Assert.Equal("en", claimed.MediaLanguagePolicy.PreferredSubtitleLanguage);
        Assert.False(claimed.MediaLanguagePolicy.AllowUnknownTrackLanguage);
    }

    private static MediaRequestEntity Request(string title, QualityProfileEntity? profile) => new()
    {
        MediaId = 46195,
        MediaType = MediaType.TvShow,
        RequestScopeKind = RequestScopeKind.Series,
        Title = title,
        Status = RequestStatus.Approved,
        IsAnime = true,
        QualityProfileId = profile?.Id
    };

    private static FulfillmentJobEntity Job(MediaRequestEntity request, int? profileId,
        string? policy = null) => new()
    {
        MediaRequestId = request.Id,
        MediaId = request.MediaId,
        MediaType = MediaType.TvShow,
        MediaKind = MediaKind.Series,
        RequestScopeKind = RequestScopeKind.Series,
        Title = request.Title,
        Status = FulfillmentStatus.Queued,
        IsAnime = true,
        QualityProfileId = profileId,
        MediaLanguagePolicyJson = policy
    };

    private sealed class FixedPreferences : ILibraryOrganizationPreferencesService
    {
        public Task<LibraryOrganizationPreferencesDto> GetAsync() =>
            Task.FromResult(new LibraryOrganizationPreferencesDto());
        public Task<bool> UpdateAsync(LibraryOrganizationPreferencesDto prefs) => Task.FromResult(false);
    }
}
