using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Organize;
using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class PlaybackPreparationTests
{
    [Fact]
    public async Task ClaimUsesLatestLegacyAuditPerDestinationAndPersistsCompletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.AddJobAsync(new MediaLanguagePolicyDto
        {
            Preference = ReleaseLanguagePreference.Smart,
            PreferredAudioLanguage = "en",
            SetPreferredTracksAsDefault = true
        });
        job.Quality = Quality.FullHD;
        job.GenresCsv = "Drama,Animation";
        job.LibraryDestinationRootPath = "/library/show";
        var older = new ImportedFileEntity
        {
            FulfillmentJobId = job.Id, DestinationPath = "/library/show/episode.mkv",
            SourcePath = "/downloads/old.mkv", FileType = "video",
            ImportedAt = DateTime.UtcNow.AddDays(-2)
        };
        var latest = new ImportedFileEntity
        {
            FulfillmentJobId = job.Id, DestinationPath = "/library/show/episode.mkv",
            SourcePath = "/downloads/new.mkv", FileType = "video",
            ImportedAt = DateTime.UtcNow.AddDays(-1)
        };
        fixture.Db.ImportedFiles.AddRange(older, latest,
            new ImportedFileEntity
            {
                FulfillmentJobId = job.Id, DestinationPath = "/library/show/current.mkv",
                SourcePath = "/downloads/current.mkv", FileType = "video",
                PlaybackPreparedAt = DateTime.UtcNow
            },
            new ImportedFileEntity
            {
                FulfillmentJobId = job.Id, DestinationPath = "/library/show/legacy.mp4",
                SourcePath = "/downloads/legacy.mp4", FileType = "video"
            });
        await fixture.Db.SaveChangesAsync();

        var claimed = await fixture.Service.ClaimAsync("worker-a", CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(latest.Id, claimed.ImportedFileId);
        Assert.Equal("en", claimed.Policy.PreferredAudioLanguage);
        Assert.Equal(Quality.FullHD, claimed.Quality);
        Assert.Equal(["Drama", "Animation"], claimed.Genres);
        Assert.Equal("/library/show", claimed.LibraryDestinationRootPath);
        // Claim and report are separate authenticated HTTP requests in production and therefore use separate
        // scoped DbContexts. Clear here to model that boundary after ClaimAsync's conditional bulk update.
        fixture.Db.ChangeTracker.Clear();
        Assert.True(await fixture.Service.ReportAsync(new PlaybackPreparationReportDto
        {
            ImportedFileId = claimed.ImportedFileId,
            WorkerId = "worker-a",
            Completed = true,
            Changed = true,
            Detail = "normalized",
            MediaTracks = Tracks([("en", true), ("ru", false)])
        }, CancellationToken.None));

        fixture.Db.ChangeTracker.Clear();
        var completed = await fixture.Db.ImportedFiles.SingleAsync(file => file.Id == latest.Id);
        Assert.NotNull(completed.PlaybackPreparedAt);
        Assert.Null(completed.PlaybackPreparationClaimedAt);
        Assert.Equal("normalized", completed.PlaybackPreparationDetail);
        Assert.Null(await fixture.Service.ClaimAsync("worker-b", CancellationToken.None));
    }

    [Fact]
    public async Task DeferredAdmissionDoesNotConsumeTheBoundedFailureBudget()
    {
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.AddJobAsync(null);
        var file = new ImportedFileEntity
        {
            FulfillmentJobId = job.Id, DestinationPath = "/library/movie.mkv",
            SourcePath = "/downloads/movie.mkv", FileType = "video"
        };
        fixture.Db.ImportedFiles.Add(file);
        await fixture.Db.SaveChangesAsync();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var claim = await fixture.Service.ClaimAsync("worker", CancellationToken.None);
            Assert.NotNull(claim);
            fixture.Db.ChangeTracker.Clear();
            Assert.True(await fixture.Service.ReportAsync(new PlaybackPreparationReportDto
            {
                ImportedFileId = claim!.ImportedFileId,
                WorkerId = "worker",
                Completed = false,
                Attempted = attempt > 0,
                Detail = attempt == 0 ? "mount offline" : "mkvmerge failed"
            }, CancellationToken.None));
            fixture.Db.ChangeTracker.Clear();
        }

        // The admission deferral did not count, so only two real failures are recorded and work remains.
        var finalClaim = await fixture.Service.ClaimAsync("worker", CancellationToken.None);
        Assert.NotNull(finalClaim);
        fixture.Db.ChangeTracker.Clear();
        Assert.True(await fixture.Service.ReportAsync(new PlaybackPreparationReportDto
        {
            ImportedFileId = finalClaim!.ImportedFileId,
            WorkerId = "worker",
            Completed = false,
            Attempted = true,
            Detail = "third failure"
        }, CancellationToken.None));
        Assert.Null(await fixture.Service.ClaimAsync("worker", CancellationToken.None));
        Assert.Equal(3, (await fixture.Db.ImportedFiles.SingleAsync()).PlaybackPreparationAttempts);
    }

    [Fact]
    public async Task FirstReleaseOutsideRootFailureIsReclaimedAfterPathResolutionFix()
    {
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.AddJobAsync(null);
        fixture.Db.ImportedFiles.Add(new ImportedFileEntity
        {
            FulfillmentJobId = job.Id,
            DestinationPath = "Show/Season 01/episode.mkv",
            SourcePath = "/downloads/episode.mkv",
            FileType = "video",
            PlaybackPreparationAttempts = 3,
            PlaybackPreparationDetail = PlaybackPreparationReportDto.LegacyOutsideRootFailure
        });
        await fixture.Db.SaveChangesAsync();

        var claim = await fixture.Service.ClaimAsync("worker", CancellationToken.None);

        Assert.NotNull(claim);
        fixture.Db.ChangeTracker.Clear();
        Assert.True(await fixture.Service.ReportAsync(new PlaybackPreparationReportDto
        {
            ImportedFileId = claim!.ImportedFileId,
            WorkerId = "worker",
            Completed = true,
            Attempted = false,
            Detail = "resolved after update"
        }, CancellationToken.None));
        Assert.NotNull((await fixture.Db.ImportedFiles.SingleAsync()).PlaybackPreparedAt);
    }

    [Fact]
    public async Task ClaimWaitsWhileAReplacementForTheSameRequestIsActive()
    {
        await using var fixture = await Fixture.CreateAsync();
        var completed = await fixture.AddJobAsync(null);
        fixture.Db.ImportedFiles.Add(new ImportedFileEntity
        {
            FulfillmentJobId = completed.Id,
            DestinationPath = "/library/show/episode.mkv",
            SourcePath = "/downloads/episode.mkv",
            FileType = "video"
        });
        fixture.Db.FulfillmentJobs.Add(new FulfillmentJobEntity
        {
            MediaRequestId = completed.MediaRequestId,
            MediaId = completed.MediaId,
            MediaType = completed.MediaType,
            Title = completed.Title,
            Status = FulfillmentStatus.Downloading
        });
        await fixture.Db.SaveChangesAsync();

        Assert.Null(await fixture.Service.ClaimAsync("worker", CancellationToken.None));
    }

    [Fact]
    public async Task LegacyJobWithoutSnapshotUsesTheConfiguredDefaultLanguageProfile()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.QualityProfiles.Add(new QualityProfileEntity
        {
            Name = "Spanish default",
            IsDefault = true,
            PreferredAudioLanguage = "es",
            PreferredSubtitleLanguage = "es",
            LanguagePreference = ReleaseLanguagePreference.EnglishPreferred,
            SetPreferredTracksAsDefault = true
        });
        var job = await fixture.AddJobAsync(null);
        fixture.Db.ImportedFiles.Add(new ImportedFileEntity
        {
            FulfillmentJobId = job.Id,
            DestinationPath = "/library/movie.mkv",
            SourcePath = "/downloads/movie.mkv",
            FileType = "video"
        });
        await fixture.Db.SaveChangesAsync();

        var claim = await fixture.Service.ClaimAsync("worker", CancellationToken.None);

        Assert.NotNull(claim);
        Assert.Equal("es", claim!.Policy.PreferredAudioLanguage);
        Assert.Equal("es", claim.Policy.PreferredSubtitleLanguage);
    }

    [Fact]
    public void LegacyTrackPreparationRequiresPhysicalPreferredOrderAndCorrectFlags()
    {
        var before = Tracks([("ru", true), ("en", false)]);
        var policy = new MediaLanguagePolicyDto
        {
            Preference = ReleaseLanguagePreference.Smart,
            PreferredAudioLanguage = "en",
            SetPreferredTracksAsDefault = true
        };
        var selection = Assert.IsType<MediaTrackDefaultSelection>(
            MediaTrackDefaultSelection.Create(policy, before, isAnime: false));

        Assert.False(LegacyPlaybackPreparationWorker.IsPrepared(before, selection));
        Assert.True(LegacyPlaybackPreparationWorker.RequiresFullRewrite(selection));

        var after = Tracks([("en", true), ("ru", false)]);
        var verified = Assert.IsType<MediaTrackDefaultSelection>(
            MediaTrackDefaultSelection.Create(policy, after, isAnime: false));
        Assert.True(LegacyPlaybackPreparationWorker.IsPrepared(after, verified));
        Assert.False(LegacyPlaybackPreparationWorker.RequiresFullRewrite(verified));
    }

    [Fact]
    public void LegacyTrackPreparationRejectsFilesOutsideConfiguredLibraryRoots()
    {
        var preferences = new EffectiveLibraryOrganization
        {
            TvPath = "/library/tv",
            MoviePath = "/library/movies"
        };

        var resolved = LegacyPlaybackPreparationWorker.ResolveLibraryPath(new PlaybackPreparationTaskDto
        {
            DestinationPath = "/library/tv/Show/episode.mkv"
        }, preferences);

        Assert.Equal(Path.GetFullPath("/library/tv"), resolved.Root);
        Assert.Throws<RetiredLibraryPathException>(() =>
            LegacyPlaybackPreparationWorker.ResolveLibraryPath(new PlaybackPreparationTaskDto
            {
                DestinationPath = "/downloads/episode.mkv"
            }, preferences));
        Assert.Throws<InvalidOperationException>(() =>
            LegacyPlaybackPreparationWorker.ResolveLibraryPath(new PlaybackPreparationTaskDto
            {
                DestinationPath = "/library/tv/Show/episode.mp4"
            }, preferences));
    }

    [Fact]
    public void LegacyRelativeAuditPathsResolveUnderTheMatchingCurrentLibraryRoot()
    {
        var preferences = new EffectiveLibraryOrganization
        {
            TvPath = "/library/tv",
            MoviePath = "/library/movies"
        };
        var task = new PlaybackPreparationTaskDto
        {
            DestinationPath = "Show (2022)/Season 01/Show - s01e01.mkv",
            MediaType = MediaType.TvShow,
            Quality = Quality.FullHD
        };

        var resolved = LegacyPlaybackPreparationWorker.ResolveLibraryPath(task, preferences);

        Assert.Equal(Path.GetFullPath("/library/tv/Show (2022)/Season 01/Show - s01e01.mkv"),
            resolved.Path);
        Assert.Equal(Path.GetFullPath("/library/tv"), resolved.Root);
        task.DestinationPath = "../movies/escape.mkv";
        Assert.Throws<InvalidOperationException>(() =>
            LegacyPlaybackPreparationWorker.ResolveLibraryPath(task, preferences));
    }

    [Fact]
    public void AtomicLegacyPreparationBreaksAHardlinkWithoutChangingTheTorrentPayload()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), $"plexrequests-playback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var torrent = Path.Combine(root, "torrent.mkv");
            var library = Path.Combine(root, "library.mkv");
            File.WriteAllText(torrent, "torrent-payload");
            Assert.Equal(0, link(torrent, library));

            AtomicLibraryFile.Replace(library, staged => File.AppendAllText(staged, "-prepared"));

            Assert.Equal("torrent-payload", File.ReadAllText(torrent));
            Assert.Equal("torrent-payload-prepared", File.ReadAllText(library));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AtomicLegacyPreparationLeavesTheOriginalIntactWhenPreparationFails()
    {
        var root = Path.Combine(Path.GetTempPath(), $"plexrequests-playback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var library = Path.Combine(root, "episode.mkv");
            File.WriteAllText(library, "valid-library-file");

            Assert.Throws<InvalidOperationException>(() => AtomicLibraryFile.Replace(library, staged =>
            {
                File.WriteAllText(staged, "broken");
                throw new InvalidOperationException("verification failed");
            }));

            Assert.Equal("valid-library-file", File.ReadAllText(library));
            Assert.DoesNotContain(Directory.EnumerateFiles(root), path => path.EndsWith(".partial"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MediaTrackSummaryDto Tracks(IEnumerable<(string Language, bool Default)> audio) => new()
    {
        HasVideo = true,
        Audio = audio.Select((track, index) => new MediaTrackDto
        {
            Index = index + 1,
            Type = "Audio",
            Language = track.Language,
            IsDefault = track.Default
        }).ToList()
    };

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldpath, string newpath);

    private sealed class Fixture(SqliteConnection connection, AppDbContext db) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public PlaybackPreparationService Service { get; } = new(
            db, NullLogger<PlaybackPreparationService>.Instance);

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async Task<FulfillmentJobEntity> AddJobAsync(MediaLanguagePolicyDto? policy)
        {
            var request = new MediaRequestEntity
            {
                MediaId = 99,
                MediaType = MediaType.TvShow,
                Title = "Show"
            };
            Db.MediaRequests.Add(request);
            await Db.SaveChangesAsync();
            var job = new FulfillmentJobEntity
            {
                MediaRequestId = request.Id,
                MediaId = request.MediaId,
                MediaType = request.MediaType,
                Title = request.Title,
                Status = FulfillmentStatus.Completed,
                CompletedAt = DateTime.UtcNow,
                MediaLanguagePolicyJson = policy is null ? null : JsonSerializer.Serialize(policy)
            };
            Db.FulfillmentJobs.Add(job);
            await Db.SaveChangesAsync();
            return job;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
