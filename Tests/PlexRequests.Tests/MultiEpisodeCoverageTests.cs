using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Organize;
using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using PlexRequestsHosted.Shared;
using Xunit;

namespace PlexRequests.Tests;

public sealed class MultiEpisodeCoverageTests
{
    private readonly ReleaseParser _parser = new();

    [Theory]
    [InlineData("Show.S02E18-E19.1080p.mkv")]
    [InlineData("Show.S02E18-S02E19.1080p.mkv")]
    [InlineData("Show.S02E18E19.1080p.mkv")]
    public void ParserRetainsEveryEpisodeInACombinedFile(string name)
    {
        var parsed = _parser.Parse(name);

        Assert.Equal(2, parsed.Season);
        Assert.Equal([18, 19], parsed.EpisodeNumbers);
        Assert.Equal(18, parsed.EpisodeStart);
        Assert.Equal(19, parsed.EpisodeEnd);
        Assert.True(parsed.IsSeasonPack);
    }

    [Fact]
    public void ParserDoesNotWidenNonContiguousCoverage()
    {
        var parsed = _parser.Parse("Show.S01E01E03.mkv");

        Assert.Equal([1, 3], parsed.EpisodeNumbers);
        Assert.DoesNotContain(2, parsed.EpisodeNumbers);
    }

    [Fact]
    public void ParserRecognizesAnimeAbsoluteEpisodeWithoutTreatingItAsAPlexSeason()
    {
        var parsed = _parser.Parse("[Group] Show - 13v2 [1080p].mkv");

        Assert.Equal(0, parsed.Season);
        Assert.Equal(13, parsed.Episode);
        Assert.Equal([13], parsed.EpisodeNumbers);
        Assert.Equal("[Group] Show", parsed.Title);
    }

    [Fact]
    public void MappingParserRejectsDuplicateTargets()
    {
        var profile = OrderProfile("A1 -> S01E01\nA2 -> S01E01");

        Assert.False(EpisodeOrderMapping.TryParse(profile, out _, out var error));
        Assert.Contains("more than one source episode", error);
    }

    [Fact]
    public void SplitterReportsRangesAndOverlapsWithoutGuessing()
    {
        var splitter = new SeasonPackSplitter(_parser, NullLogger<SeasonPackSplitter>.Instance);

        var range = splitter.Map(["Show.S01E01-E02.mkv"], 1, 12);
        Assert.True(range.IsUnambiguous);
        Assert.Equal([1, 2], Assert.Single(range.Mappings).Episodes);

        var conflict = splitter.Map(["Show.S01E01-E02.mkv", "Show.S01E02.mkv"], 1, 12);
        Assert.Equal([2], conflict.ConflictingEpisodes);

        var unknown = splitter.Map(["Segment.A.mkv", "Segment.B.mkv"], 1, 2);
        Assert.Empty(unknown.Mappings);
        Assert.Equal(2, unknown.UnmappedFiles.Count);

        var nonContiguous = splitter.Map(["Show.S01E01E03.mkv"], 1, 3);
        Assert.False(nonContiguous.IsUnambiguous);
        Assert.Empty(nonContiguous.Mappings);
    }

    [Fact]
    public void RangeNamingExtendsExistingTemplatesWithPlexNotation()
    {
        var naming = new PlexNamingService();
        var prefs = new EffectiveLibraryOrganization { TvPath = "/library/tv" };
        var job = TvJob("/library/tv");

        var path = naming.BuildEpisodeRangePath(prefs, job, 2, 18, 19, ".mkv");

        Assert.True(path.EndsWith("Show - s02e18-e19.mkv", StringComparison.OrdinalIgnoreCase), path);
    }

    [Fact]
    public async Task OrganizerImportsOneRangeFileWithDurableCoverage()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "Show.S01E01-E02.mkv");
            await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
            var library = Path.Combine(root, "library");
            var organizer = CreateOrganizer();

            var result = await organizer.OrganizeAsync(TvJob(library),
                new TransferItem("transfer", 1, null, true, NeededEpisodes: [1, 2]), source,
                Preferences(), CancellationToken.None);

            Assert.True(result.Success, result.FailReason);
            var video = Assert.Single(result.Files, x => x.FileType == "video");
            Assert.True(video.DestinationPath.EndsWith("Show - s01e01-e02.mkv",
                StringComparison.OrdinalIgnoreCase), video.DestinationPath);
            Assert.Equal([(1, 1), (1, 2)], video.EpisodeCoverage!
                .Select(x => (x.Season, x.Episode)).ToList());
            Assert.True(File.Exists(video.DestinationPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OrganizerRejectsNumberingMismatchBeforeAnyLibraryWrite()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "Show.S01E01.mkv");
            await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
            var library = Path.Combine(root, "library");

            var result = await CreateOrganizer().OrganizeAsync(TvJob(library),
                new TransferItem("transfer", 1, null, true, NeededEpisodes: [2]), source,
                Preferences(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(BlocklistReason.EpisodeMappingAmbiguous, result.BlocklistReason);
            Assert.False(Directory.Exists(library));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OrganizerTranslatesAbsolutePackFileToCanonicalPlexEpisode()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "Show - 13.mkv");
            await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
            var library = Path.Combine(root, "library");
            var job = TvJob(library);
            job.EpisodeOrderProfile = OrderProfile("A13 -> S02E01");

            var result = await CreateOrganizer().OrganizeAsync(job,
                new TransferItem("transfer", 2, null, true, NeededEpisodes: [1]), source,
                Preferences(), CancellationToken.None);

            Assert.True(result.Success, result.FailReason);
            var video = Assert.Single(result.Files, x => x.FileType == "video");
            Assert.True(video.DestinationPath.EndsWith("Show - s02e01.mkv", StringComparison.OrdinalIgnoreCase),
                video.DestinationPath);
            Assert.Equal([(2, 1)], video.EpisodeCoverage!.Select(x => (x.Season, x.Episode)).ToList());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OrganizerRejectsMissingAbsoluteMappingBeforeWriting()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "Show - 14.mkv");
            await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
            var library = Path.Combine(root, "library");
            var job = TvJob(library);
            job.EpisodeOrderProfile = OrderProfile("A13 -> S02E01");

            var result = await CreateOrganizer().OrganizeAsync(job,
                new TransferItem("transfer", 2, null, true, NeededEpisodes: [1]), source,
                Preferences(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(BlocklistReason.EpisodeMappingAmbiguous, result.BlocklistReason);
            Assert.False(Directory.Exists(library));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OrganizerImportsOneAbsolutePackAcrossCanonicalSeasons()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "pack");
            Directory.CreateDirectory(source);
            await File.WriteAllBytesAsync(Path.Combine(source, "Show - 13.mkv"), [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(Path.Combine(source, "Show - 14.mkv"), [1, 2, 3, 4]);
            var library = Path.Combine(root, "library");
            var job = TvJob(library);
            job.EpisodeOrderProfile = OrderProfile("A13 -> S01E01\nA14 -> S02E01");
            var targets = new[]
            {
                new EpisodeRef { Season = 1, Episode = 1 },
                new EpisodeRef { Season = 2, Episode = 1 }
            };

            var result = await CreateOrganizer().OrganizeAsync(job,
                new TransferItem("transfer", null, null, true, NeededEpisodeRefs: targets), source,
                Preferences(), CancellationToken.None);

            Assert.True(result.Success, result.FailReason);
            var videos = result.Files.Where(x => x.FileType == "video")
                .OrderBy(x => x.Season).ToList();
            Assert.Equal(2, videos.Count);
            Assert.Equal([(1, 1), (2, 1)], videos.Select(x => (x.Season!.Value, x.Episode!.Value)).ToList());
            Assert.Contains(videos, x => x.DestinationPath.EndsWith("Show - s01e01.mkv", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(videos, x => x.DestinationPath.EndsWith("Show - s02e01.mkv", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OrganizerRejectsCrossSeasonPackMissingACanonicalTargetBeforeWriting()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "pack");
            Directory.CreateDirectory(source);
            await File.WriteAllBytesAsync(Path.Combine(source, "Show - 13.mkv"), [1, 2, 3, 4]);
            var library = Path.Combine(root, "library");
            var job = TvJob(library);
            job.EpisodeOrderProfile = OrderProfile("A13 -> S01E01\nA14 -> S02E01");

            var result = await CreateOrganizer().OrganizeAsync(job,
                new TransferItem("transfer", null, null, true, NeededEpisodeRefs:
                [
                    new EpisodeRef { Season = 1, Episode = 1 },
                    new EpisodeRef { Season = 2, Episode = 1 }
                ]), source, Preferences(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(BlocklistReason.EpisodeMappingAmbiguous, result.BlocklistReason);
            Assert.False(Directory.Exists(library));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ImportAuditPersistsOnePhysicalFileToManyEpisodes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();

        var request = new MediaRequestEntity
        {
            MediaId = 123,
            MediaType = MediaType.TvShow,
            Title = "Show",
            RequestedBy = "tester"
        };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();
        var job = new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            Title = request.Title
        };
        db.FulfillmentJobs.Add(job);
        await db.SaveChangesAsync();
        db.ImportedFiles.Add(new ImportedFileEntity
        {
            FulfillmentJobId = job.Id,
            SourcePath = "/downloads/show.mkv",
            DestinationPath = "/library/Show - s01e01-e02.mkv",
            FileType = "video",
            SeasonNumber = 1,
            EpisodeNumber = 1,
            EpisodeCoverage =
            [
                new() { SeasonNumber = 1, EpisodeNumber = 1 },
                new() { SeasonNumber = 1, EpisodeNumber = 2 }
            ]
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var imported = await db.ImportedFiles.Include(x => x.EpisodeCoverage).SingleAsync();
        Assert.Equal([(1, 1), (1, 2)], imported.EpisodeCoverage
            .OrderBy(x => x.EpisodeNumber).Select(x => (x.SeasonNumber, x.EpisodeNumber)).ToList());
    }

    private LibraryOrganizer CreateOrganizer() => new(
        new NoArchives(), new SeasonPackSplitter(_parser, NullLogger<SeasonPackSplitter>.Instance),
        new TwoEpisodes(), new PlexNamingService(), _parser, new NoInspection(),
        NullLogger<LibraryOrganizer>.Instance);

    private static EffectiveLibraryOrganization Preferences() => new()
    {
        TransferMode = TransferMode.Copy,
        MinVideoFileSizeMb = 0,
        KeepSubtitles = false,
        SplitSeasonPacks = true
    };

    private static FulfillmentJobDto TvJob(string library) => new()
    {
        Id = 20,
        Title = "Show",
        Year = 2026,
        MediaType = MediaType.TvShow,
        TmdbId = 123,
        LibraryDestination = new LibraryDestinationSnapshotDto
        {
            Id = "kids-tv",
            Name = "Kids TV",
            ContentKind = LibraryContentKind.Series,
            RootPath = library,
            Template = "{ShowTitle} ({Year})/Season {Season:00}/{ShowTitle} - s{Season:00}e{Episode:00} - {EpisodeTitle}{Ext}",
            SeasonPackFolderTemplate = "{ShowTitle} ({Year})/Season {Season:00}"
        }
    };

    private static SeriesEpisodeOrderProfileDto OrderProfile(string mappings) => new()
    {
        TmdbId = 123,
        SeriesTitle = "Show",
        SourceOrder = EpisodeOrderType.Absolute,
        MappingsText = mappings,
        Enabled = true
    };

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plexrequests-coverage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class NoArchives : IArchiveExtractor
    {
        public bool LooksLikeArchive(string filePath) => false;
        public bool IsContinuationVolume(string filePath) => false;
        public Task ExtractAsync(string archiveFilePath, string destinationDirectory, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class TwoEpisodes : IEpisodeTitleProvider
    {
        public Task<IReadOnlyList<EpisodeDto>> GetSeasonEpisodesAsync(int? tmdbId, int season,
            CancellationToken ct) => Task.FromResult<IReadOnlyList<EpisodeDto>>
            ([new() { SeasonNumber = season, EpisodeNumber = 1 }, new() { SeasonNumber = season, EpisodeNumber = 2 }]);

        public Task<string?> GetEpisodeTitleAsync(int? tmdbId, int season, int episode,
            CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class NoInspection : IMediaTrackInspector
    {
        public Task<MediaTrackSummaryDto> InspectAsync(string mediaPath,
            IReadOnlyList<string> companionSubtitles, CancellationToken ct) =>
            Task.FromResult(new MediaTrackSummaryDto());
    }
}
