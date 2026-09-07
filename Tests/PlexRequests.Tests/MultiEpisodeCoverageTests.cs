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
    [InlineData(true, 2, 2, true)]
    [InlineData(true, 1, 2, false)]
    [InlineData(false, 2, 2, false)]
    [InlineData(true, 0, 0, false)]
    public void ReplacementOnlyFinalizesAfterEveryTargetImports(
        bool coversAllTargets, int importedCount, int transferCount, bool expected)
    {
        Assert.Equal(expected, FulfillmentPipeline.ReplacementReadyToFinalize(
            coversAllTargets, importedCount, transferCount));
    }

    [Fact]
    public void MissingTransferRequiresTheWholeGraceWindowBeforeFailure()
    {
        var firstSeen = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        var grace = TimeSpan.FromSeconds(30);

        Assert.False(FulfillmentPipeline.MissingTransferGraceExpired(
            firstSeen, firstSeen.AddSeconds(29), grace));
        Assert.True(FulfillmentPipeline.MissingTransferGraceExpired(
            firstSeen, firstSeen.AddSeconds(30), grace));
    }

    [Theory]
    [InlineData("Show.S02E18-E19.1080p.mkv")]
    [InlineData("Show.S02E18-S02E19.1080p.mkv")]
    [InlineData("Show.S02E18E19.1080p.mkv")]
    [InlineData("Show.2x18-19.1080p.mkv")]
    [InlineData("Show.2x18-2x19.1080p.mkv")]
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

    [Theory]
    [InlineData("Show.S01E06.5.1080p.mkv")]
    [InlineData("Show.1x06.5.1080p.mkv")]
    [InlineData("[Group] Show - 06.5 (1080p).mkv")]
    [InlineData("[Group] Show - 06.5v2 (1080p).mkv")]
    public void ParserNeverTruncatesFractionalSpecialsToIntegerEpisodes(string name)
    {
        var parsed = _parser.Parse(name);

        Assert.True(parsed.FractionalEpisodeNumber);
        Assert.Null(parsed.Season);
        Assert.Null(parsed.Episode);
        Assert.Empty(parsed.EpisodeNumbers);
    }

    [Fact]
    public void ParserPreservesFractionalSourceIdentityForManifestProof()
    {
        var standard = _parser.Parse("Show.S01E06.5v2.1080p.mkv");
        var absolute = _parser.Parse("[Group] Show - 06.5 (1080p).mkv");

        Assert.Equal((1, 6.5m), (standard.FractionalEpisodeSeason, standard.FractionalEpisode));
        Assert.Equal((0, 6.5m), (absolute.FractionalEpisodeSeason, absolute.FractionalEpisode));
    }

    [Theory]
    [InlineData("Show.S01E06.1080p.mkv")]
    [InlineData("Show.S01E06v2.1080p.mkv")]
    [InlineData("Show.1x06v2.1080p.mkv")]
    public void ParserKeepsOrdinaryAndVersionedEpisodesDistinctFromFractions(string name)
    {
        var parsed = _parser.Parse(name);

        Assert.False(parsed.FractionalEpisodeNumber);
        Assert.Equal(1, parsed.Season);
        Assert.Equal(6, parsed.Episode);
        Assert.Equal([6], parsed.EpisodeNumbers);
    }

    [Fact]
    public void ParserRetainsAnimeAbsoluteRangeAsOneCombinedFile()
    {
        var parsed = _parser.Parse("[Group] Show - 13-14 [1080p].mkv");

        Assert.Equal(0, parsed.Season);
        Assert.Null(parsed.Episode);
        Assert.Equal([13, 14], parsed.EpisodeNumbers);
        Assert.Equal(13, parsed.EpisodeStart);
        Assert.Equal(14, parsed.EpisodeEnd);
        Assert.True(parsed.IsSeasonPack);
        Assert.Equal("[Group] Show", parsed.Title);
    }

    [Theory]
    [InlineData("Show.S01E01-S02E02.1080p.mkv")]
    [InlineData("Show.1x01-2x02.1080p.mkv")]
    [InlineData("Show.1x02-01.1080p.mkv")]
    public void ParserDoesNotDowngradeAnInvalidRangeToItsFirstEpisode(string name)
    {
        var parsed = _parser.Parse(name);

        Assert.Null(parsed.Season);
        Assert.Null(parsed.Episode);
        Assert.Empty(parsed.EpisodeNumbers);
        Assert.False(parsed.IsSeasonPack);
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

        var translated = splitter.Map(["Anime.S04E01.mkv", "Anime.S04E02.mkv"],
            season: 3, expectedEpisodeCount: 23, sourceSeason: 4);
        Assert.True(translated.IsUnambiguous);
        Assert.All(translated.Mappings, mapping => Assert.Equal(3, mapping.Season));

        var wrongSource = splitter.Map(["Anime.S05E01.mkv"],
            season: 3, expectedEpisodeCount: 23, sourceSeason: 4);
        Assert.False(wrongSource.IsUnambiguous);

        var absoluteRejected = splitter.Map(["Anime - 01.mkv"],
            season: 5, expectedEpisodeCount: 2, sourceSeason: 1);
        Assert.False(absoluteRejected.IsUnambiguous);

        var namedAbsolute = splitter.Map(["Anime - 01.mkv", "Anime - 02.mkv"],
            season: 5, expectedEpisodeCount: 2, sourceSeason: 1, allowAbsoluteOrder: true);
        Assert.True(namedAbsolute.IsUnambiguous);
        Assert.All(namedAbsolute.Mappings, mapping => Assert.Equal(5, mapping.Season));
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
    public async Task OrganizerRejectsFractionalSpecialBeforeAnyLibraryWrite()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "Show.S01E01.5.mkv");
            await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
            var library = Path.Combine(root, "library");

            var result = await CreateOrganizer().OrganizeAsync(TvJob(library),
                new TransferItem("transfer", 1, null, true, NeededEpisodes: [1]), source,
                Preferences(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(BlocklistReason.EpisodeMappingAmbiguous, result.BlocklistReason);
            Assert.False(Directory.Exists(library));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OrganizerReplaysManifestProvenFractionalInsertionForDownloadedSubset()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "Monogatari Series Off & Monster Season");
            Directory.CreateDirectory(source);
            foreach (var episode in new[] { "06", "06.5", "07", "14" })
                await File.WriteAllBytesAsync(Path.Combine(source,
                    $"Monogatari Series Off & Monster Season - {episode}.mkv"), [1, 2, 3, 4]);
            var library = Path.Combine(root, "library");
            var job = TvJob(library);
            job.Title = "Monogatari";
            job.IsAnime = true;
            job.SeasonTargets =
            [
                new SeasonTarget
                {
                    Season = 5,
                    Name = "MONOGATARI Series OFF & MONSTER Season",
                    EpisodeCount = 15,
                    MissingEpisodes = [6, 7, 8, 15]
                }
            ];
            job.CanonicalSeasons =
            [
                new CanonicalSeasonIdentityDto
                {
                    Season = 5,
                    Name = "MONOGATARI Series OFF & MONSTER Season",
                    EpisodeCount = 15
                }
            ];
            var targets = new[] { 6, 7, 8, 15 }
                .Select(episode => new EpisodeRef { Season = 5, Episode = episode }).ToList();

            var result = await CreateOrganizer().OrganizeAsync(job,
                new TransferItem("transfer", 5, null, true, NeededEpisodeRefs: targets,
                    SourceSeason: 1, FractionalEpisodeInsertionAfter: 6),
                source, Preferences(), CancellationToken.None);

            Assert.True(result.Success, result.FailReason);
            var episodes = result.Files.Where(file => file.FileType == "video")
                .Select(file => file.Episode!.Value).Order().ToList();
            Assert.Equal([6, 7, 8, 15], episodes);
            Assert.All(result.Files.Where(file => file.FileType == "video"), file =>
                Assert.True(File.Exists(file.DestinationPath)));
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
    public async Task OrganizerImportsNamedSeasonAbsoluteFilesUnderTheCanonicalSeason()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "Monogatari Series Off & Monster Season");
            Directory.CreateDirectory(source);
            await File.WriteAllBytesAsync(
                Path.Combine(source, "Monogatari Series Off & Monster Season - 01.mkv"), [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(
                Path.Combine(source, "Monogatari Series Off & Monster Season - 02.mkv"), [1, 2, 3, 4]);
            var library = Path.Combine(root, "library");
            var job = TvJob(library);
            job.Title = "Monogatari";
            job.IsAnime = true;
            job.CanonicalSeasons =
            [
                new CanonicalSeasonIdentityDto
                {
                    Season = 5,
                    Name = "MONOGATARI Series OFF & MONSTER Season",
                    EpisodeCount = 2
                }
            ];

            var result = await CreateOrganizer().OrganizeAsync(job,
                new TransferItem("transfer", 5, null, true, NeededEpisodes: [1, 2], SourceSeason: 1),
                source, Preferences(), CancellationToken.None);

            Assert.True(result.Success, result.FailReason);
            var videos = result.Files.Where(file => file.FileType == "video")
                .OrderBy(file => file.Episode).ToList();
            Assert.Equal([(5, 1), (5, 2)], videos.Select(file =>
                (file.Season!.Value, file.Episode!.Value)).ToList());
            Assert.All(videos, file => Assert.Contains("Season 05", file.DestinationPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OrganizerRejectsBareAbsoluteFilesWithoutNamedSeasonIdentity()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "pack");
            Directory.CreateDirectory(source);
            await File.WriteAllBytesAsync(Path.Combine(source, "Monogatari - 01.mkv"), [1, 2, 3, 4]);
            var library = Path.Combine(root, "library");
            var job = TvJob(library);
            job.Title = "Monogatari";
            job.IsAnime = true;
            job.CanonicalSeasons =
            [
                new CanonicalSeasonIdentityDto
                {
                    Season = 5,
                    Name = "MONOGATARI Series OFF & MONSTER Season",
                    EpisodeCount = 2
                }
            ];

            var result = await CreateOrganizer().OrganizeAsync(job,
                new TransferItem("transfer", 5, null, true, NeededEpisodes: [1], SourceSeason: 1),
                source, Preferences(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(BlocklistReason.EpisodeMappingAmbiguous, result.BlocklistReason);
            Assert.False(Directory.Exists(library));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OrganizerImportsAbsoluteCombinedFileWithinOneCanonicalSeason()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "Show - 13-14.mkv");
            await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
            var library = Path.Combine(root, "library");
            var job = TvJob(library);
            job.EpisodeOrderProfile = OrderProfile("A13 -> S02E01\nA14 -> S02E02");

            var result = await CreateOrganizer().OrganizeAsync(job,
                new TransferItem("transfer", 2, null, true, NeededEpisodes: [1, 2]), source,
                Preferences(), CancellationToken.None);

            Assert.True(result.Success, result.FailReason);
            var video = Assert.Single(result.Files, x => x.FileType == "video");
            Assert.True(video.DestinationPath.EndsWith("Show - s02e01-e02.mkv",
                StringComparison.OrdinalIgnoreCase), video.DestinationPath);
            Assert.Equal([(2, 1), (2, 2)], video.EpisodeCoverage!
                .Select(x => (x.Season, x.Episode)).ToList());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OrganizerRejectsOneCombinedFileMappedAcrossCanonicalSeasons()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "Show - 13-14.mkv");
            await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
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
    public async Task OrganizerUsesNumberedAnimeArcFolderWithExplicitEpisodeGroupMapping()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "pack");
            var arc = Path.Combine(source, "01 - Bakemonogatari");
            Directory.CreateDirectory(arc);
            await File.WriteAllBytesAsync(Path.Combine(arc, "[MTBB] Bakemonogatari - 01v2 [346DABB1].mkv"), [1, 2, 3, 4]);
            var library = Path.Combine(root, "library");
            var job = TvJob(library);
            job.Title = "Monogatari";
            job.EpisodeOrderProfile = OrderProfile("S01E01 -> S01E01");
            job.EpisodeOrderProfile.SourceOrder = EpisodeOrderType.Custom;

            var result = await CreateOrganizer().OrganizeAsync(job,
                new TransferItem("transfer", null, null, true, NeededEpisodeRefs:
                [new EpisodeRef { Season = 1, Episode = 1 }]), source,
                Preferences(), CancellationToken.None);

            Assert.True(result.Success, result.FailReason);
            var video = Assert.Single(result.Files, x => x.FileType == "video");
            Assert.Equal((1, 1), (video.Season, video.Episode));
            Assert.True(video.DestinationPath.EndsWith("Monogatari - s01e01.mkv",
                StringComparison.OrdinalIgnoreCase), video.DestinationPath);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void NumberedFolderAndAbsoluteEvidenceMustNotDisagree()
    {
        var profile = OrderProfile("A1 -> S01E01\nS01E01 -> S02E01");
        profile.SourceOrder = EpisodeOrderType.Custom;

        Assert.False(EpisodeOrderMapping.TryTranslateFile(profile,
            Path.Combine("01 - Arc", "Show - 01.mkv"), 0, 1, out _));
    }

    [Fact]
    public void ForcedCollectionRetainsEveryCanonicalSeasonTarget()
    {
        var job = TvJob("/library");
        job.IsAnime = true;
        job.IsManualGrab = true;
        job.ForcedMagnet = "magnet:?xt=urn:btih:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        job.ForcedReleaseName = "[Group] Anime Collection";
        job.RequestedSeasons = [1, 2];
        job.SeasonTargets =
        [
            new SeasonTarget { Season = 1, EpisodeCount = 2, MissingEpisodes = [1, 2] },
            new SeasonTarget { Season = 2, EpisodeCount = 1, MissingEpisodes = [1] }
        ];

        var plan = FulfillmentPipeline.BuildForcedPlan(job);

        var item = Assert.Single(plan.Items);
        Assert.Null(item.Season);
        Assert.Null(item.NeededEpisodes);
        Assert.True(item.RequiresManifestPreflight);
        Assert.Equal([(1, 1), (1, 2), (2, 1)],
            item.NeededEpisodeRefs!.Select(target => (target.Season, target.Episode)).ToList());
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
            Task.FromResult(new MediaTrackSummaryDto { HasVideo = true });
    }
}
