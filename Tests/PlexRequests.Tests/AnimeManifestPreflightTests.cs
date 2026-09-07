using Microsoft.Extensions.Logging.Abstractions;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Download;
using PlexRequests.Downloader.Ranking;
using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

public sealed class AnimeManifestPreflightTests
{
    private readonly ReleaseParser _parser = new();
    private static readonly string[] VideoExtensions = [".mkv", ".mp4"];
    private static readonly string[] SubtitleExtensions = [".srt", ".ass"];

    [Fact]
    public void NamedCollectionSelectsOnlyMissingCanonicalSeasons()
    {
        var (job, item) = NamedCollectionJobAndItem();
        var manifest = Manifest(
            ("01 - Bakemonogatari/[MTBB] Bakemonogatari - 01v2.mkv", GiB(1)),
            ("01 - Bakemonogatari/[MTBB] Bakemonogatari - 02v2.mkv", GiB(1)),
            ("01 - Bakemonogatari/[MTBB] Bakemonogatari - 03.mkv", GiB(1)),
            ("02 - Nisemonogatari/[MTBB] Nisemonogatari - 01.mkv", GiB(1)),
            ("03 - Owarimonogatari/[MTBB] Owarimonogatari - 01.mkv", GiB(1)),
            ("Extras/[MTBB] Monogatari NCOP.mkv", GiB(.1)),
            ("01 - Bakemonogatari/[MTBB] Bakemonogatari - 01v2.en.ass", 10_000));

        var result = AnimeManifestPreflight.Evaluate(manifest, job, item, _parser,
            VideoExtensions, maxSelectedGb: 10, subtitleExtensions: SubtitleExtensions);

        Assert.True(result.Accepted, result.Detail);
        Assert.Equal([true, true, false, true, false, false, true], result.WantedFiles);
        Assert.Equal([(1, 1), (1, 2), (2, 1)], result.CanonicalCoverage
            .Select(episode => (episode.Season, episode.Episode)).ToList());
    }

    [Fact]
    public void NamedCollectionRejectsAFileThatMixesTargetAndNonTargetEpisodes()
    {
        var (job, item) = NamedCollectionJobAndItem();

        var result = AnimeManifestPreflight.Evaluate(
            Manifest(("01 - Bakemonogatari/[MTBB] Bakemonogatari - 01-03.mkv", GiB(2)),
                     ("02 - Nisemonogatari/[MTBB] Nisemonogatari - 01.mkv", GiB(1))),
            job, item, _parser, VideoExtensions, maxSelectedGb: 10);

        Assert.False(result.Accepted);
        Assert.Contains("combines requested episodes", result.Detail);
        Assert.DoesNotContain(true, result.WantedFiles);
    }

    [Fact]
    public void NamedCollectionRejectsDuplicateCanonicalCoverage()
    {
        var (job, item) = NamedCollectionJobAndItem();

        var result = AnimeManifestPreflight.Evaluate(
            Manifest(("01 - Bakemonogatari/[A] Bakemonogatari - 01.mkv", GiB(1)),
                     ("01 - Bakemonogatari/[B] Bakemonogatari - 01.mkv", GiB(1)),
                     ("01 - Bakemonogatari/[MTBB] Bakemonogatari - 02.mkv", GiB(1)),
                     ("02 - Nisemonogatari/[MTBB] Nisemonogatari - 01.mkv", GiB(1))),
            job, item, _parser, VideoExtensions, maxSelectedGb: 10);

        Assert.False(result.Accepted);
        Assert.Contains("appears in both", result.Detail);
        Assert.DoesNotContain(true, result.WantedFiles);
    }

    [Fact]
    public void NamedCollectionDoesNotFallBackToBareUploaderSeasonNumbers()
    {
        var (job, item) = NamedCollectionJobAndItem();

        var result = AnimeManifestPreflight.Evaluate(
            Manifest(("Unknown Arc/[Group] Monogatari.S01E01.mkv", GiB(1)),
                     ("Unknown Arc/[Group] Monogatari.S01E02.mkv", GiB(1)),
                     ("Unknown Arc/[Group] Monogatari.S02E01.mkv", GiB(1))),
            job, item, _parser, VideoExtensions, maxSelectedGb: 10);

        Assert.False(result.Accepted);
        Assert.Contains("numbered video file(s) did not match", result.Detail);
        Assert.DoesNotContain(true, result.WantedFiles);
    }

    [Fact]
    public void PostAddSelectionRepeatsNamedCollectionContract()
    {
        var (job, _) = NamedCollectionJobAndItem();
        var transfer = new TransferItem("hash", null, null, true, NeededEpisodeRefs:
        [
            new EpisodeRef { Season = 1, Episode = 1 },
            new EpisodeRef { Season = 1, Episode = 2 },
            new EpisodeRef { Season = 2, Episode = 1 }
        ]);

        var selected = FulfillmentPipeline.BuildCanonicalPackFileSelection(job, transfer,
        [
            "01 - Bakemonogatari/[MTBB] Bakemonogatari - 01v2.mkv",
            "01 - Bakemonogatari/[MTBB] Bakemonogatari - 02v2.mkv",
            "01 - Bakemonogatari/[MTBB] Bakemonogatari - 03.mkv",
            "02 - Nisemonogatari/[MTBB] Nisemonogatari - 01.mkv",
            "03 - Owarimonogatari/[MTBB] Owarimonogatari - 01.mkv",
            "Unknown Arc/[Group] Monogatari.S01E01.mkv"
        ], _parser, VideoExtensions, SubtitleExtensions);

        Assert.Empty(selected.MissingCoverage);
        Assert.Equal([true, true, false, true, false, false], selected.Keep);
    }

    [Fact]
    public void NamedCollectionPlanUsesProvableSeasonsAndLeavesAnAmbiguousArcForContinuation()
    {
        var (job, item) = NamedCollectionJobAndItem();
        var expandedItem = item with
        {
            NeededEpisodeRefs = item.NeededEpisodeRefs!
                .Append(new EpisodeRef { Season = 4, Episode = 1 }).ToList()
        };
        var manifest = Manifest(
            ("01 - Bakemonogatari/[MTBB] Bakemonogatari - 01v2.mkv", GiB(1)),
            ("01 - Bakemonogatari/[MTBB] Bakemonogatari - 02v2.mkv", GiB(1)),
            ("03 - Nisemonogatari/[MTBB] Nisemonogatari - 01v2.mkv", GiB(1)),
            ("13 - Owarimonogatari S1/[MTBB] Owarimonogatari S1 - 01.mkv", GiB(1)),
            ("14 - Owarimonogatari S2/[MTBB] Owarimonogatari S2 - 01.mkv", GiB(1)));

        var result = FulfillmentPipeline.TryPlanNamedAnimeCollection(manifest, job, expandedItem,
            _parser, VideoExtensions, maxSelectedGb: 10, subtitleExtensions: SubtitleExtensions);

        Assert.NotNull(result);
        Assert.False(result.Value.Plan.CoversAllTargets);
        var planned = Assert.Single(result.Value.Plan.Items);
        Assert.Equal([(1, 1), (1, 2), (2, 1)], planned.NeededEpisodeRefs!
            .Select(target => (target.Season, target.Episode)).ToList());
        Assert.Equal([true, true, true, false, false], result.Value.Decision.WantedFiles);
    }

    [Fact]
    public void NumberedArcManifestSelectsOnlyTheExactCanonicalTargetSet()
    {
        var (job, item) = JobAndItem();
        var manifest = Manifest(
            ("01 - Bakemonogatari/[MTBB] Bakemonogatari - 01.mkv", GiB(1)),
            ("01 - Bakemonogatari/[MTBB] Bakemonogatari - 02.mkv", GiB(1)),
            ("02 - Nisemonogatari/[MTBB] Nisemonogatari - 01.mkv", GiB(1)),
            ("Extras/[MTBB] NCOP.mkv", GiB(.1)),
            ("01 - Bakemonogatari/subtitle.ass", 10_000));

        var result = AnimeManifestPreflight.Evaluate(manifest, job, item, _parser,
            VideoExtensions, maxSelectedGb: 10, subtitleExtensions: SubtitleExtensions);

        Assert.True(result.Accepted, result.Detail);
        Assert.Equal([true, true, true, false, true], result.WantedFiles);
        Assert.Equal([(1, 1), (1, 2), (2, 1)], result.CanonicalCoverage
            .Select(episode => (episode.Season, episode.Episode)).ToList());
        Assert.Contains("selected 4/5 files", result.Detail);
    }

    [Fact]
    public void LivePackTrimDoesNotReenableUnnumberedVideoExtras()
    {
        var job = TestData.Job("Monogatari", MediaType.TvShow,
            seasonTargets: [TestData.Season(4, 1, 2)]);
        job.IsAnime = true;
        var transfer = new TransferItem("hash", 4, null, true,
            NeededEpisodeRefs:
            [
                new EpisodeRef { Season = 4, Episode = 1 },
                new EpisodeRef { Season = 4, Episode = 2 }
            ]);
        string[] files =
        [
            "Monogatari/Monogatari.S04E01.mkv",
            "Monogatari/Monogatari.S04E02.mkv",
            "Monogatari/Monogatari.S04.NCOP.02.mkv",
            "Monogatari/Monogatari.S04.NCED.01.mkv",
            "Monogatari/Monogatari.S04E01.en.ass",
            "Monogatari/readme.nfo"
        ];

        var result = FulfillmentPipeline.BuildCanonicalPackFileSelection(job, transfer, files,
            _parser, VideoExtensions, SubtitleExtensions);

        Assert.Empty(result.MissingCoverage);
        Assert.Equal([true, true, false, false, true, false], result.Keep);
    }

    [Fact]
    public void MissingCanonicalEpisodeRejectsTheManifest()
    {
        var (job, item) = JobAndItem();
        var manifest = Manifest(
            ("01 - Bakemonogatari/[MTBB] Bakemonogatari - 01.mkv", GiB(1)),
            ("02 - Nisemonogatari/[MTBB] Nisemonogatari - 01.mkv", GiB(1)));

        var result = AnimeManifestPreflight.Evaluate(manifest, job, item, _parser,
            VideoExtensions, maxSelectedGb: 10);

        Assert.False(result.Accepted);
        Assert.Contains("S01E02", result.Detail);
        Assert.DoesNotContain(true, result.WantedFiles);
    }

    [Fact]
    public void NamedSeasonTranslationMapsUploaderSeasonToCanonicalSeason()
    {
        var (job, _) = JobAndItem();
        job.EpisodeOrderProfile = null;
        var item = new DownloadPlanItem(TestData.Release("Monogatari Second Season S04"), 3, null, true)
        {
            SourceSeason = 4,
            NeededEpisodeRefs =
            [
                new EpisodeRef { Season = 3, Episode = 1 },
                new EpisodeRef { Season = 3, Episode = 2 }
            ],
            RequiresManifestPreflight = true
        };

        var result = AnimeManifestPreflight.Evaluate(
            Manifest(("[Group] Monogatari S04E01.mkv", GiB(1)),
                     ("[Group] Monogatari S04E02.mkv", GiB(1))),
            job, item, _parser, VideoExtensions, maxSelectedGb: 10);

        Assert.True(result.Accepted, result.Detail);
        Assert.Equal([(3, 1), (3, 2)], result.CanonicalCoverage
            .Select(episode => (episode.Season, episode.Episode)).ToList());
    }

    [Fact]
    public void NamedSeasonTranslationRejectsAConflictingInternalSeason()
    {
        var (job, _) = JobAndItem();
        job.EpisodeOrderProfile = null;
        var item = new DownloadPlanItem(TestData.Release("Monogatari Second Season S04"), 3, null, true)
        {
            SourceSeason = 4,
            NeededEpisodeRefs = [new EpisodeRef { Season = 3, Episode = 1 }],
            RequiresManifestPreflight = true
        };

        var result = AnimeManifestPreflight.Evaluate(
            Manifest(("[Group] Monogatari S05E01.mkv", GiB(1))),
            job, item, _parser, VideoExtensions, maxSelectedGb: 10);

        Assert.False(result.Accepted);
        Assert.Contains("did not match", result.Detail);
    }

    [Fact]
    public void PostAddSelectionPreservesNamedSeasonTranslation()
    {
        var (job, _) = JobAndItem();
        job.EpisodeOrderProfile = null;
        var transfer = new TransferItem("hash", 3, null, true,
            NeededEpisodeRefs:
            [
                new EpisodeRef { Season = 3, Episode = 1 },
                new EpisodeRef { Season = 3, Episode = 2 }
            ],
            SourceSeason: 4);

        var selected = FulfillmentPipeline.BuildCanonicalPackFileSelection(job, transfer,
            ["Monogatari.S04E01.mkv", "Monogatari.S04E02.mkv", "Monogatari.S05E01.mkv"],
            _parser, VideoExtensions, [".ass"]);

        Assert.Empty(selected.MissingCoverage);
        Assert.Equal([true, true, false], selected.Keep);
    }

    [Fact]
    public void NamedSeasonTranslationAcceptsGapFreeAbsoluteEpisodeFilenames()
    {
        var (job, item) = NamedSeasonAbsoluteJobAndItem();

        var result = AnimeManifestPreflight.Evaluate(
            Manifest(
                ("[MTBB] Monogatari Series Off & Monster Season - 01 (BD 1080p).mkv", GiB(1)),
                ("[MTBB] Monogatari Series Off & Monster Season - 02 (BD 1080p).mkv", GiB(1))),
            job, item, _parser, VideoExtensions, maxSelectedGb: 10);

        Assert.True(result.Accepted, result.Detail);
        Assert.Equal([(5, 1), (5, 2)], result.CanonicalCoverage
            .Select(episode => (episode.Season, episode.Episode)).ToList());
    }

    [Theory]
    [InlineData("[MTBB] Monogatari - 01 (BD 1080p).mkv")]
    [InlineData("[MTBB] Monogatari Series Second Season - 01 (BD 1080p).mkv")]
    [InlineData("[MTBB] Different Show Off & Monster Season - 01 (BD 1080p).mkv")]
    public void AbsoluteEpisodeFilenameMustRepeatTheUniqueNamedSeasonIdentity(string filename)
    {
        var (job, item) = NamedSeasonAbsoluteJobAndItem();

        var result = AnimeManifestPreflight.Evaluate(
            Manifest((filename, GiB(1)),
                     ("[MTBB] Monogatari Series Off & Monster Season - 02 (BD 1080p).mkv", GiB(1))),
            job, item, _parser, VideoExtensions, maxSelectedGb: 10);

        Assert.False(result.Accepted);
        Assert.Contains("did not match", result.Detail);
    }

    [Fact]
    public void NamedSeasonAbsoluteEpisodeOutsideCanonicalCountIsRejected()
    {
        var (job, item) = NamedSeasonAbsoluteJobAndItem();

        var result = AnimeManifestPreflight.Evaluate(
            Manifest(
                ("[MTBB] Monogatari Series Off & Monster Season - 01 (BD 1080p).mkv", GiB(1)),
                ("[MTBB] Monogatari Series Off & Monster Season - 03 (BD 1080p).mkv", GiB(1))),
            job, item, _parser, VideoExtensions, maxSelectedGb: 10);

        Assert.False(result.Accepted);
        Assert.Contains("outside canonical S05", result.Detail);
    }

    [Fact]
    public void FractionalNamedSeasonEpisodeCannotSatisfyTheIntegerTarget()
    {
        var (job, item) = NamedSeasonAbsoluteJobAndItem();

        var result = AnimeManifestPreflight.Evaluate(
            Manifest(
                ("[MTBB] Monogatari Series Off & Monster Season - 01.5 (BD 1080p).mkv", GiB(1)),
                ("[MTBB] Monogatari Series Off & Monster Season - 02 (BD 1080p).mkv", GiB(1))),
            job, item, _parser, VideoExtensions, maxSelectedGb: 10);

        Assert.False(result.Accepted);
        Assert.Contains("fractional/special episode", result.Detail);
        Assert.DoesNotContain(true, result.WantedFiles);
    }

    [Fact]
    public void CompleteNamedSeasonSequenceMapsOneHalfEpisodeByPosition()
    {
        var (job, item) = FractionalNamedSeasonJobAndItem(
            Enumerable.Range(1, 15).ToArray());

        var result = AnimeManifestPreflight.Evaluate(
            Manifest(OffMonsterSeasonFiles()), job, item, _parser, VideoExtensions, maxSelectedGb: 20);

        Assert.True(result.Accepted, result.Detail);
        Assert.Equal(6, result.FractionalEpisodeInsertionAfter);
        Assert.Equal(Enumerable.Range(1, 15), result.CanonicalCoverage.Select(episode => episode.Episode));
        Assert.All(result.WantedFiles, Assert.True);
    }

    [Fact]
    public void FractionalSequenceAlsoMapsWhenUploaderAndCanonicalSeasonNumbersAgree()
    {
        var (job, original) = FractionalNamedSeasonJobAndItem([7, 15]);
        var item = original with { SourceSeason = 5 };

        var result = AnimeManifestPreflight.Evaluate(
            Manifest(OffMonsterSeasonFiles()), job, item, _parser, VideoExtensions, maxSelectedGb: 20);

        Assert.True(result.Accepted, result.Detail);
        Assert.Equal(6, result.FractionalEpisodeInsertionAfter);
        Assert.Equal([7, 15], result.CanonicalCoverage.Select(episode => episode.Episode).ToList());
    }

    [Fact]
    public void CompleteNamedSeasonSequenceSelectsOnlyRequestedCanonicalPositions()
    {
        var (job, item) = FractionalNamedSeasonJobAndItem([7, 8, 15]);

        var result = AnimeManifestPreflight.Evaluate(
            Manifest(OffMonsterSeasonFiles()), job, item, _parser, VideoExtensions, maxSelectedGb: 20);

        Assert.True(result.Accepted, result.Detail);
        Assert.Equal(6, result.FractionalEpisodeInsertionAfter);
        Assert.Equal([7, 8, 15], result.CanonicalCoverage.Select(episode => episode.Episode).ToList());
        Assert.Equal([6, 7, 14], result.WantedFiles.Select((wanted, index) => (wanted, index))
            .Where(pair => pair.wanted).Select(pair => pair.index).ToList());
    }

    [Fact]
    public void GappedFractionalSequenceRequiresAdminReview()
    {
        var (job, item) = FractionalNamedSeasonJobAndItem([1, 15]);
        var files = OffMonsterSeasonFiles().Where(file => !file.Path.Contains(" - 14 ")).ToArray();

        var result = AnimeManifestPreflight.Evaluate(
            Manifest(files), job, item, _parser, VideoExtensions, maxSelectedGb: 20);

        Assert.False(result.Accepted);
        Assert.Contains("cannot prove a complete sequence", result.Detail);
        Assert.DoesNotContain(true, result.WantedFiles);
    }

    [Fact]
    public void MultipleFractionalInsertionsRequireAdminReview()
    {
        var (job, item) = FractionalNamedSeasonJobAndItem([1, 15]);
        var files = OffMonsterSeasonFiles()
            .Append(("Monogatari Series Off & Monster Season/" +
                     "[MiniMTBB] Monogatari Series Off & Monster Season - 09.5 (BD 1080p).mkv", GiB(1)))
            .ToArray();

        var result = AnimeManifestPreflight.Evaluate(
            Manifest(files), job, item, _parser, VideoExtensions, maxSelectedGb: 20);

        Assert.False(result.Accepted);
        Assert.Contains("2 fractional episodes", result.Detail);
    }

    [Fact]
    public void NonHalfFractionRequiresExplicitMappingOrAdminReview()
    {
        var (job, item) = FractionalNamedSeasonJobAndItem([1, 15]);
        var files = OffMonsterSeasonFiles().Select(file =>
            file.Path.Contains(" - 06.5 ")
                ? (file.Path.Replace(" - 06.5 ", " - 06.25 "), file.Bytes)
                : file).ToArray();

        var result = AnimeManifestPreflight.Evaluate(
            Manifest(files), job, item, _parser, VideoExtensions, maxSelectedGb: 20);

        Assert.False(result.Accepted);
        Assert.Contains("explicit episode-order mapping or admin review", result.Detail);
    }

    [Fact]
    public void PostAddSelectionReplaysFrozenFractionalInsertion()
    {
        var (job, _) = FractionalNamedSeasonJobAndItem([7, 8, 15]);
        var transfer = new TransferItem("hash", 5, null, true,
            NeededEpisodeRefs:
            [
                new EpisodeRef { Season = 5, Episode = 7 },
                new EpisodeRef { Season = 5, Episode = 8 },
                new EpisodeRef { Season = 5, Episode = 15 }
            ],
            SourceSeason: 1,
            FractionalEpisodeInsertionAfter: 6);

        var selected = FulfillmentPipeline.BuildCanonicalPackFileSelection(job, transfer,
        [
            "Monogatari Series Off & Monster Season/Monogatari Series Off & Monster Season - 06.mkv",
            "Monogatari Series Off & Monster Season/Monogatari Series Off & Monster Season - 06.5.mkv",
            "Monogatari Series Off & Monster Season/Monogatari Series Off & Monster Season - 07.mkv",
            "Monogatari Series Off & Monster Season/Monogatari Series Off & Monster Season - 14.mkv"
        ], _parser, VideoExtensions, SubtitleExtensions);

        Assert.Empty(selected.MissingCoverage);
        Assert.Equal([false, true, true, true], selected.Keep);
    }

    [Fact]
    public void PostAddSelectionKeepsOnlyNamedSeasonAbsoluteEpisodes()
    {
        var (job, _) = NamedSeasonAbsoluteJobAndItem();
        var transfer = new TransferItem("hash", 5, null, true,
            NeededEpisodeRefs:
            [
                new EpisodeRef { Season = 5, Episode = 1 },
                new EpisodeRef { Season = 5, Episode = 2 }
            ],
            SourceSeason: 1);

        var selected = FulfillmentPipeline.BuildCanonicalPackFileSelection(job, transfer,
        [
            "Monogatari Series Off & Monster Season/Monogatari Series Off & Monster Season - 01.mkv",
            "Monogatari Series Off & Monster Season/Monogatari Series Off & Monster Season - 02.mkv",
            "Monogatari Series Second Season/Monogatari Series Second Season - 01.mkv"
        ], _parser, VideoExtensions, [".ass"]);

        Assert.Empty(selected.MissingCoverage);
        Assert.Equal([true, true, false], selected.Keep);
    }

    [Fact]
    public void DuplicateCanonicalEpisodeRejectsTheManifest()
    {
        var (job, item) = JobAndItem();
        var manifest = Manifest(
            ("01 - Bakemonogatari/[MTBB] Bakemonogatari - 01.mkv", GiB(1)),
            ("01 - Bakemonogatari/[Alt] Bakemonogatari - 01v2.mkv", GiB(1)),
            ("01 - Bakemonogatari/[MTBB] Bakemonogatari - 02.mkv", GiB(1)),
            ("02 - Nisemonogatari/[MTBB] Nisemonogatari - 01.mkv", GiB(1)));

        var result = AnimeManifestPreflight.Evaluate(manifest, job, item, _parser,
            VideoExtensions, maxSelectedGb: 10);

        Assert.False(result.Accepted);
        Assert.Contains("appears in both", result.Detail);
    }

    [Theory]
    [InlineData("../escape/Show - 01.mkv")]
    [InlineData("/absolute/Show - 01.mkv")]
    public void UnsafeManifestPathRejectsTheWholeTorrent(string path)
    {
        var (job, item) = JobAndItem();

        var result = AnimeManifestPreflight.Evaluate(Manifest((path, GiB(1))),
            job, item, _parser, VideoExtensions, maxSelectedGb: 10);

        Assert.False(result.Accepted);
        Assert.Contains("unsafe path", result.Detail);
    }

    [Fact]
    public void SelectedPayloadMustStayWithinThePackLimit()
    {
        var (job, item) = JobAndItem();
        var manifest = Manifest(
            ("01 - Bakemonogatari/[MTBB] Bakemonogatari - 01.mkv", GiB(4)),
            ("01 - Bakemonogatari/[MTBB] Bakemonogatari - 02.mkv", GiB(4)),
            ("02 - Nisemonogatari/[MTBB] Nisemonogatari - 01.mkv", GiB(4)));

        var result = AnimeManifestPreflight.Evaluate(manifest, job, item, _parser,
            VideoExtensions, maxSelectedGb: 10);

        Assert.False(result.Accepted);
        Assert.Contains("12.0 GB", result.Detail);
    }

    [Fact]
    public void RankerUsesUnscopedCollectionOnlyAsCompletePreflightFallback()
    {
        var (job, _) = JobAndItem();
        job.QualityProfile = TestData.Profile(TestData.Definitions());
        job.QualityDefinitions = TestData.Definitions();
        job.EmptySearchCount = 3;
        var ranker = CreateRanker();

        var plan = ranker.PlanDownload(
            [TestData.Release("[MTBB] Monogatari Series (BD 1080p)", sizeGb: 70.2)], job);

        Assert.False(plan.IsEmpty, ranker.LastFailureSummary);
        var item = Assert.Single(plan.Items);
        Assert.True(item.RequiresManifestPreflight);
        Assert.True(plan.CoversAllTargets);
        Assert.Equal([(1, 1), (1, 2), (2, 1)], item.NeededEpisodeRefs!
            .Select(episode => (episode.Season, episode.Episode)).ToList());
    }

    [Fact]
    public void RankerDoesNotUseCollectionWithoutACompleteExplicitMap()
    {
        var (job, _) = JobAndItem();
        job.QualityProfile = TestData.Profile(TestData.Definitions());
        job.QualityDefinitions = TestData.Definitions();
        job.EmptySearchCount = 3;
        job.EpisodeOrderProfile!.MappingsText = "S01E01 -> S01E01";

        var plan = CreateRanker().PlanDownload(
            [TestData.Release("[MTBB] Monogatari Series (BD 1080p)", sizeGb: 70.2)], job);

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void CompleteSeriesClaimStillRequiresManifestPreflightWithoutAnAlternateMap()
    {
        var job = TestData.Job("Show", MediaType.TvShow, seasonTargets:
        [
            TestData.Season(1, 1, 2),
            TestData.Season(2, 1)
        ]);
        job.IsAnime = true;

        var plan = CreateRanker().PlanDownload(
            [TestData.Release("Show.COMPLETE.SERIES.1080p.WEB-DL", sizeGb: 30)], job);

        var item = Assert.Single(plan.Items);
        Assert.True(item.RequiresManifestPreflight);
        Assert.Equal([(1, 1), (1, 2), (2, 1)], item.NeededEpisodeRefs!
            .Select(episode => (episode.Season, episode.Episode)).ToList());
    }

    [Fact]
    public void OrdinaryAnimeSeasonPackAlsoRequiresManifestPreflight()
    {
        var job = TestData.Job("Show", MediaType.TvShow,
            seasonTargets: [TestData.Season(1, 1, 2)]);
        job.IsAnime = true;

        var plan = CreateRanker().PlanDownload(
            [TestData.Release("Show.S01.1080p.WEB-DL", sizeGb: 4)], job);

        Assert.True(Assert.Single(plan.Items).RequiresManifestPreflight);
    }

    [Fact]
    public void IdentityNumberedSeasonManifestDoesNotNeedAnAlternateOrderMap()
    {
        var job = TestData.Job("Show", MediaType.TvShow,
            seasonTargets: [TestData.Season(1, 1, 2)]);
        job.IsAnime = true;
        var item = new DownloadPlanItem(TestData.Release("Show.S01.1080p.WEB-DL"), 1, null, true)
        {
            NeededEpisodeRefs =
            [
                new EpisodeRef { Season = 1, Episode = 1 },
                new EpisodeRef { Season = 1, Episode = 2 }
            ],
            RequiresManifestPreflight = true
        };
        var manifest = Manifest(
            ("Show/Show.S01E01.mkv", GiB(1)),
            ("Show/Show.S01E02.mkv", GiB(1)));

        var result = AnimeManifestPreflight.Evaluate(manifest, job, item, _parser,
            VideoExtensions, maxSelectedGb: 10);

        Assert.True(result.Accepted, result.Detail);
        Assert.Equal([true, true], result.WantedFiles);
    }

    [Fact]
    public void ResolverSelectsTheOnlyEpisodeGroupWhoseFullManifestMatches()
    {
        var (job, item) = JobAndItem();
        job.EpisodeOrderProfile = null;
        var manifest = Manifest(
            ("01 - Bakemonogatari/[MTBB] Bakemonogatari - 01.mkv", GiB(1)),
            ("01 - Bakemonogatari/[MTBB] Bakemonogatari - 02.mkv", GiB(1)),
            ("02 - Nisemonogatari/[MTBB] Nisemonogatari - 01.mkv", GiB(1)));
        var matching = Profile("matching", "Story order",
            "S01E01 -> S01E01\nS01E02 -> S01E02\nS02E01 -> S02E01",
            (1, "Bakemonogatari"), (2, "Nisemonogatari"));
        var wrong = Profile("wrong", "Wrong order", "S01E01 -> S01E01\nS01E02 -> S01E02",
            (1, "Bakemonogatari"));

        var result = AnimeEpisodeOrderResolver.Resolve(manifest, job, item, [wrong, matching],
            _parser, VideoExtensions, maxSelectedGb: 10);

        Assert.True(result.Resolved, result.Detail);
        Assert.Equal("matching", result.Profile!.SourceEpisodeGroupId);
        Assert.True(result.Decision!.Accepted);
        Assert.Contains("uniquely matches", result.Detail);
    }

    [Fact]
    public void ResolverRejectsConflictingGroupsThatBothFitTheCurrentManifest()
    {
        var (job, item) = JobAndItem();
        job.EpisodeOrderProfile = null;
        var manifest = Manifest(
            ("01 - Arc/[Group] Arc - 01.mkv", GiB(1)),
            ("01 - Arc/[Group] Arc - 02.mkv", GiB(1)),
            ("02 - Arc/[Group] Arc - 01.mkv", GiB(1)));
        var first = Profile("first", "First order",
            "S01E01 -> S01E01\nS01E02 -> S01E02\nS02E01 -> S02E01", (1, "Arc"), (2, "Arc"));
        var second = Profile("second", "Second order",
            "S01E01 -> S01E02\nS01E02 -> S01E01\nS02E01 -> S02E01", (1, "Arc"), (2, "Arc"));

        var result = AnimeEpisodeOrderResolver.Resolve(manifest, job, item, [first, second],
            _parser, VideoExtensions, maxSelectedGb: 10);

        Assert.False(result.Resolved);
        Assert.Contains("conflicting", result.Detail);
        Assert.Contains("admin review", result.Detail);
    }

    [Fact]
    public void ResolverTreatsDuplicateOfficialGroupsWithTheSameFullContractAsOneMatch()
    {
        var (job, item) = JobAndItem();
        job.EpisodeOrderProfile = null;
        var manifest = Manifest(
            ("01 - Arc/[Group] Arc - 01.mkv", GiB(1)),
            ("01 - Arc/[Group] Arc - 02.mkv", GiB(1)),
            ("02 - Arc/[Group] Arc - 01.mkv", GiB(1)));
        const string map = "S01E01 -> S01E01\nS01E02 -> S01E02\nS02E01 -> S02E01";

        var result = AnimeEpisodeOrderResolver.Resolve(manifest, job, item,
            [Profile("b", "Same B", map, (1, "Arc"), (2, "Arc")),
             Profile("a", "Same A", map, (1, "Arc"), (2, "Arc"))],
            _parser, VideoExtensions, maxSelectedGb: 10);

        Assert.True(result.Resolved, result.Detail);
        Assert.Equal("a", result.Profile!.SourceEpisodeGroupId);
    }

    [Fact]
    public void RankerExposesOnlyCollectionsBlockedSolelyByManifestScope()
    {
        var (job, _) = JobAndItem();
        job.EpisodeOrderProfile = null;
        job.QualityProfile = TestData.Profile(TestData.Definitions());
        job.QualityDefinitions = TestData.Definitions();
        job.EmptySearchCount = 3;
        var safe = TestData.Release("[MTBB] Monogatari Series (BD 1080p)", sizeGb: 70.2,
            infoHash: new string('a', 40));
        var unhealthy = TestData.Release("[Other] Monogatari Series (BD 1080p)", seeders: 0,
            sizeGb: 70.2, infoHash: new string('b', 40));
        var ranker = CreateRanker();

        Assert.True(ranker.PlanDownload([unhealthy, safe], job).IsEmpty);

        var candidate = Assert.Single(ranker.ManifestFallbackCandidates);
        Assert.Equal(safe.Acquisition.SourceId, candidate.Acquisition.SourceId);
    }

    [Fact]
    public void OfficialGroupNamePreventsOrdinalCollisionBetweenAnimeArcs()
    {
        var (job, _) = JobAndItem();
        job.EpisodeOrderProfile = Profile("novel", "Novel order",
            "S01E01 -> S01E01\nS02E01 -> S02E01",
            (1, "Bakemonogatari"), (2, "Nisemonogatari"));
        var item = new DownloadPlanItem(TestData.Release("Monogatari collection"), null, null, true)
        {
            NeededEpisodeRefs = [new EpisodeRef { Season = 2, Episode = 1 }],
            RequiresManifestPreflight = true
        };

        var result = AnimeManifestPreflight.Evaluate(
            Manifest(("02 - Kizumonogatari/[MTBB] Kizumonogatari - 01.mkv", GiB(1))),
            job, item, _parser, VideoExtensions, maxSelectedGb: 10);

        Assert.False(result.Accepted);
        Assert.DoesNotContain(true, result.WantedFiles);
        Assert.Contains("S02E01", result.Detail);
        Assert.Contains("Kizumonogatari", result.Detail);
    }

    [Fact]
    public void StructuralSeasonSuffixMayMatchAnAuthoritativeArcName()
    {
        var profile = Profile("novel", "Novel order", "S12E01 -> S04E01",
            (12, "Owarimonogatari"));

        Assert.True(EpisodeOrderMapping.TryTranslateFile(profile,
            "12 - Owarimonogatari S1/[MTBB] Owarimonogatari S1 - 01.mkv",
            parsedSeason: 1, sourceEpisode: 1, out var target));
        Assert.Equal((4, 1), (target.Season, target.Episode));
    }

    private static ReleaseRankerAdapter CreateRanker() => new(
        TestData.Evaluator(), new DownloadPlanner(), new StubPreferences(), new StubIndexers(),
        NullLogger<ReleaseRankerAdapter>.Instance);

    private static (FulfillmentJobDto Job, DownloadPlanItem Item) JobAndItem()
    {
        var targets = new List<EpisodeRef>
        {
            new() { Season = 1, Episode = 1 },
            new() { Season = 1, Episode = 2 },
            new() { Season = 2, Episode = 1 }
        };
        var job = TestData.Job("Monogatari", MediaType.TvShow, seasonTargets:
        [
            TestData.Season(1, 1, 2),
            TestData.Season(2, 1)
        ]);
        job.IsAnime = true;
        job.TmdbId = 46195;
        job.RequestScope = RequestScopeKind.Series;
        job.EpisodeOrderProfile = new SeriesEpisodeOrderProfileDto
        {
            TmdbId = 46195,
            SourceOrder = EpisodeOrderType.Custom,
            MappingsText = "S01E01 -> S01E01\nS01E02 -> S01E02\nS02E01 -> S02E01"
        };
        var item = new DownloadPlanItem(
            TestData.Release("[MTBB] Monogatari Series (BD 1080p)", sizeGb: 70.2),
            null, null, true)
        {
            NeededEpisodeRefs = targets,
            RequiresManifestPreflight = true
        };
        return (job, item);
    }

    private static (FulfillmentJobDto Job, DownloadPlanItem Item) NamedSeasonAbsoluteJobAndItem()
    {
        var targets = new List<EpisodeRef>
        {
            new() { Season = 5, Episode = 1 },
            new() { Season = 5, Episode = 2 }
        };
        var job = TestData.Job("Monogatari", MediaType.TvShow, seasonTargets:
        [
            new SeasonTarget
            {
                Season = 5,
                Name = "MONOGATARI Series OFF & MONSTER Season",
                EpisodeCount = 2,
                MissingEpisodes = [1, 2]
            }
        ]);
        job.IsAnime = true;
        job.TmdbId = 46195;
        job.RequestScope = RequestScopeKind.Seasons;
        job.CanonicalSeasons =
        [
            new CanonicalSeasonIdentityDto
            {
                Season = 3,
                Name = "Monogatari Series: Second Season",
                EpisodeCount = 23
            },
            new CanonicalSeasonIdentityDto
            {
                Season = 5,
                Name = "MONOGATARI Series OFF & MONSTER Season",
                EpisodeCount = 2
            }
        ];
        var item = new DownloadPlanItem(
            TestData.Release("[MTBB] Monogatari Series Off & Monster Season S1 (BD 1080p)"),
            5, null, true)
        {
            SourceSeason = 1,
            NeededEpisodeRefs = targets,
            RequiresManifestPreflight = true
        };
        return (job, item);
    }

    private static (FulfillmentJobDto Job, DownloadPlanItem Item) NamedCollectionJobAndItem()
    {
        var job = TestData.Job("Monogatari", MediaType.TvShow, seasonTargets:
        [
            new SeasonTarget { Season = 1, Name = "Bakemonogatari", EpisodeCount = 3, MissingEpisodes = [1, 2] },
            new SeasonTarget { Season = 2, Name = "Nisemonogatari", EpisodeCount = 1, MissingEpisodes = [1] }
        ]);
        job.IsAnime = true;
        job.TmdbId = 46195;
        job.RequestScope = RequestScopeKind.Series;
        job.EpisodeOrderProfile = null;
        job.CanonicalSeasons =
        [
            new CanonicalSeasonIdentityDto { Season = 1, Name = "Bakemonogatari", EpisodeCount = 3 },
            new CanonicalSeasonIdentityDto { Season = 2, Name = "Nisemonogatari", EpisodeCount = 1 },
            new CanonicalSeasonIdentityDto { Season = 4, Name = "Owarimonogatari", EpisodeCount = 1 }
        ];
        var targets = new List<EpisodeRef>
        {
            new() { Season = 1, Episode = 1 },
            new() { Season = 1, Episode = 2 },
            new() { Season = 2, Episode = 1 }
        };
        var item = new DownloadPlanItem(
            TestData.Release("[MTBB] Monogatari Series (BD 1080p)", sizeGb: 70.2),
            null, null, true)
        {
            NeededEpisodeRefs = targets,
            RequiresManifestPreflight = true
        };
        return (job, item);
    }

    private static (FulfillmentJobDto Job, DownloadPlanItem Item) FractionalNamedSeasonJobAndItem(
        IReadOnlyCollection<int> wantedEpisodes)
    {
        var job = TestData.Job("Monogatari", MediaType.TvShow, seasonTargets:
        [
            new SeasonTarget
            {
                Season = 5,
                Name = "MONOGATARI Series OFF & MONSTER Season",
                EpisodeCount = 15,
                MissingEpisodes = wantedEpisodes.Order().ToList()
            }
        ]);
        job.IsAnime = true;
        job.TmdbId = 46195;
        job.RequestScope = RequestScopeKind.Seasons;
        job.CanonicalSeasons =
        [
            new CanonicalSeasonIdentityDto
            {
                Season = 5,
                Name = "MONOGATARI Series OFF & MONSTER Season",
                EpisodeCount = 15
            }
        ];
        var item = new DownloadPlanItem(
            TestData.Release("[MiniMTBB] Monogatari Series Off & Monster Season S1 (BD 1080p)"),
            5, null, true)
        {
            SourceSeason = 1,
            NeededEpisodeRefs = wantedEpisodes.Order()
                .Select(episode => new EpisodeRef { Season = 5, Episode = episode }).ToList(),
            RequiresManifestPreflight = true
        };
        return (job, item);
    }

    private static (string Path, long Bytes)[] OffMonsterSeasonFiles() =>
        Enumerable.Range(1, 14)
            .Select(episode => ($"Monogatari Series Off & Monster Season/" +
                                $"[MiniMTBB] Monogatari Series Off & Monster Season - {episode:00} (BD 1080p).mkv",
                GiB(1)))
            .Append(("Monogatari Series Off & Monster Season/" +
                     "[MiniMTBB] Monogatari Series Off & Monster Season - 06.5 (BD 1080p).mkv", GiB(1)))
            .OrderBy(file => file.Item1, StringComparer.Ordinal)
            .ToArray();

    private static SeriesEpisodeOrderProfileDto Profile(string id, string name, string mappings,
        params (int Season, string Name)[] groups) => new()
    {
        TmdbId = 46195,
        SeriesTitle = "Monogatari",
        SourceOrder = EpisodeOrderType.Custom,
        SourceEpisodeGroupId = id,
        SourceEpisodeGroupName = name,
        SourceGroups = groups.Select(group => new EpisodeOrderSourceGroupDto
        {
            SourceSeason = group.Season,
            Name = group.Name
        }).ToList(),
        MappingsText = mappings,
        Enabled = true
    };

    private static AcquisitionManifest Manifest(params (string Path, long Bytes)[] files) =>
        new(files.Select(file => new AcquisitionManifestFile(file.Path, file.Bytes)).ToList());

    private static long GiB(double value) => (long)(value * 1024 * 1024 * 1024);

    private sealed class StubPreferences : IDownloadPreferencesProvider
    {
        public EffectiveDownloadPreferences Current { get; } = new()
        {
            MinSeeders = 1,
            MaxSizeGb = 25,
            MaxSeasonPackSizeGb = 80,
            MinTitleSimilarity = .5,
            SeasonPackStrategy = SeasonPackStrategy.PreferPack
        };
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubIndexers : IIndexerSettingsProvider
    {
        public IReadOnlyList<IndexerConfigDto> All { get; } = [];
        public int PriorityOf(int indexerId) => 25;
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
