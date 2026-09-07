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
            VideoExtensions, maxSelectedGb: 10);

        Assert.True(result.Accepted, result.Detail);
        Assert.Equal([true, true, true, false, false], result.WantedFiles);
        Assert.Equal([(1, 1), (1, 2), (2, 1)], result.CanonicalCoverage
            .Select(episode => (episode.Season, episode.Episode)).ToList());
        Assert.Contains("selected 3/5 files", result.Detail);
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
