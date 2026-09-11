using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using Xunit;

namespace PlexRequests.Tests;

public sealed class PlexPathMappingTests
{
    [Fact]
    public async Task PreferencesPersistNormalizedMappings()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var service = new LibraryOrganizationPreferencesService(db);
        var preferences = Preferences();
        preferences.PlexPathMappings =
        [
            new()
            {
                PlexSectionId = " 1 ", PlexPathPrefix = "/plex/Movies/",
                ManagedPathPrefix = "/mnt/library/Movies/"
            }
        ];

        Assert.True(await service.UpdateAsync(preferences));
        db.ChangeTracker.Clear();
        var saved = Assert.Single((await service.GetAsync()).PlexPathMappings);

        Assert.Equal("1", saved.PlexSectionId);
        Assert.Equal("/plex/Movies", saved.PlexPathPrefix);
        Assert.Equal("/mnt/library/Movies", saved.ManagedPathPrefix);
    }

    [Fact]
    public void MappingIsSectionScopedBoundarySafeAndUsesLongestPrefix()
    {
        var mappings = new List<PlexPathMappingDto>
        {
            new() { PlexSectionId = "1", PlexPathPrefix = "/plex/Movies", ManagedPathPrefix = "/mnt/library/Movies" },
            new() { PlexSectionId = "1", PlexPathPrefix = "/plex/Movies/Anime", ManagedPathPrefix = "/mnt/library/Anime Movies" }
        };

        Assert.True(PlexLibraryPathMapping.TryMap("1", "/plex/Movies/Anime/Akira.mkv", mappings,
            out var mapped));
        Assert.Equal("/mnt/library/Anime Movies/Akira.mkv", mapped);
        Assert.False(PlexLibraryPathMapping.TryMap("2", "/plex/Movies/Akira.mkv", mappings, out _));
        Assert.False(PlexLibraryPathMapping.TryMap("1", "/plex/Movies-old/Akira.mkv", mappings, out _));
        Assert.False(PlexLibraryPathMapping.TryMap("1", "/plex/Movies/../private/file.mkv", mappings, out _));
    }

    [Fact]
    public void PreferencesRejectRootAndDuplicateMappings()
    {
        var preferences = Preferences();
        preferences.PlexPathMappings =
        [
            new() { PlexSectionId = "1", PlexPathPrefix = "/", ManagedPathPrefix = "/mnt/library" }
        ];
        Assert.False(LibraryRouting.TryNormalizeAndValidate(preferences, out var rootError));
        Assert.Contains("non-root", rootError);

        preferences = Preferences();
        preferences.PlexPathMappings =
        [
            new() { PlexSectionId = "1", PlexPathPrefix = "/plex/Movies/", ManagedPathPrefix = "/mnt/a" },
            new() { PlexSectionId = "1", PlexPathPrefix = "/plex/Movies", ManagedPathPrefix = "/mnt/b" }
        ];
        Assert.False(LibraryRouting.TryNormalizeAndValidate(preferences, out var duplicateError));
        Assert.Contains("mapped more than once", duplicateError);
    }

    [Fact]
    public void ReplacementDeletionRevalidatesManagedRootAndReviewedSize()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"plexrequests-path-safety-{Guid.NewGuid():N}");
        var libraryRoot = Path.Combine(tempRoot, "library");
        var siblingRoot = Path.Combine(tempRoot, "library-old");
        Directory.CreateDirectory(libraryRoot);
        Directory.CreateDirectory(siblingRoot);
        var file = Path.Combine(libraryRoot, "movie.mkv");
        var sibling = Path.Combine(siblingRoot, "movie.mkv");
        try
        {
            File.WriteAllBytes(file, [1, 2, 3, 4]);
            File.WriteAllBytes(sibling, [1, 2, 3, 4]);
            var job = new FulfillmentJobDto
            {
                StorageOptimizationPolicy = new StorageOptimizationPolicyDto
                {
                    Targets = [new StorageOptimizationTargetDto { DestinationPath = file, CurrentSizeBytes = 4 }]
                }
            };
            var preferences = new EffectiveLibraryOrganization
            {
                PlexPathMappings =
                [
                    new PlexPathMappingDto
                    {
                        PlexSectionId = "1", PlexPathPrefix = "/plex/Movies", ManagedPathPrefix = libraryRoot
                    }
                ]
            };

            Assert.True(ReplacementPathSafety.CanDelete(file, job, preferences, out _));
            Assert.False(ReplacementPathSafety.CanDelete(sibling, job, preferences, out var outsideReason));
            Assert.Contains("outside", outsideReason);

            File.WriteAllBytes(file, [1, 2, 3, 4, 5]);
            Assert.False(ReplacementPathSafety.CanDelete(file, job, preferences, out var changedReason));
            Assert.Contains("changed after preview", changedReason);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    private static LibraryOrganizationPreferencesDto Preferences() => new()
    {
        MoviePath = "/library/movies",
        TvPath = "/library/tv",
        MusicPath = "/library/music"
    };
}
