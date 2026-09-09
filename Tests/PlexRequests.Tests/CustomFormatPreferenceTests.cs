using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class CustomFormatPreferenceTests
{
    [Fact]
    public async Task HumanPreferencePersistsHardRulesSeparatelyFromAdvancedScoreMath()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.QualityProfiles.Add(new QualityProfileEntity { Id = 1, Name = "Everyday" });
        db.CustomFormats.Add(new CustomFormatEntity { Id = 9, Name = "HEVC", Enabled = true });
        await db.SaveChangesAsync();
        var service = new CustomFormatService(db, NullLogger<CustomFormatService>.Instance);

        Assert.True(await service.SetPreferenceAsync(1, 9, CustomFormatPreference.Require));
        var required = await service.GetForProfileAsync(1);
        Assert.Equal(CustomFormatPreference.Require, required.Single(item => item.Id == 9).Preference);
        Assert.Equal("9", (await db.QualityProfiles.FindAsync(1))!.RequiredCustomFormatIdsCsv);
        Assert.Equal(50, await db.CustomFormatScores.Where(row => row.QualityProfileId == 1
            && row.CustomFormatId == 9).Select(row => row.Score).SingleAsync());

        Assert.True(await service.SetPreferenceAsync(1, 9, CustomFormatPreference.Block));
        db.ChangeTracker.Clear();
        var profile = await db.QualityProfiles.FindAsync(1);
        Assert.Null(profile!.RequiredCustomFormatIdsCsv);
        Assert.Equal("9", profile.BlockedCustomFormatIdsCsv);
        Assert.Equal(-50, await db.CustomFormatScores.Where(row => row.QualityProfileId == 1
            && row.CustomFormatId == 9).Select(row => row.Score).SingleAsync());
    }
}
