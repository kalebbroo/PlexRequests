using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

/// <summary>
/// Planning behaviour, with the three bugs this phase fixed pinned as regression tests.
/// </summary>
public class DownloadPlannerTests
{
    private readonly ReleaseEvaluator _eval = TestData.Evaluator();
    private readonly DownloadPlanner _planner = new();

    private DownloadPlanResult Plan(IEnumerable<ReleaseCandidate> releases, FulfillmentJobDto job, RankingContext? ctx = null)
    {
        var context = ctx ?? TestData.Context();
        return _planner.Plan(_eval.EvaluateAll(releases, job, context), job, context);
    }

    [Fact]
    public void Picks_the_single_best_release_for_a_movie()
    {
        var job = TestData.Job(title: "Dune", type: MediaType.Movie, year: 2021);
        var result = Plan(new[]
        {
            TestData.Release("Dune.2021.720p.WEB-DL", seeders: 900),
            TestData.Release("Dune.2021.1080p.WEB-DL", seeders: 100),
        }, job);

        Assert.Equal(DownloadPlanKind.Single, result.Plan.Kind);
        Assert.Contains("1080p", result.Plan.Items.Single().Candidate.ReleaseName);
    }

    [Fact]
    public void Prefers_a_season_pack_over_individual_episodes()
    {
        var job = TestData.Job(seasonTargets: new() { TestData.Season(2, 1, 2, 3) });
        var result = Plan(new[]
        {
            TestData.Release("Severance.S02.1080p.WEB-DL", sizeGb: 9),
            TestData.Release("Severance.S02E01.1080p.WEB-DL"),
            TestData.Release("Severance.S02E02.1080p.WEB-DL"),
            TestData.Release("Severance.S02E03.1080p.WEB-DL"),
        }, job);

        Assert.Equal(DownloadPlanKind.SeasonPack, result.Plan.Kind);
        var item = Assert.Single(result.Plan.Items);
        Assert.True(item.IsPack);
        Assert.Equal(new[] { 1, 2, 3 }, item.NeededEpisodes);
    }

    // REGRESSION: a pack whose name declares a span shorter than what's missing used to be accepted
    // outright, and the request quietly ended up with only part of the season.
    [Fact]
    public void Rejects_a_pack_that_cannot_cover_the_missing_episodes()
    {
        var job = TestData.Job(seasonTargets: new() { TestData.Season(1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13) });
        var result = Plan(new[] { TestData.Release("Severance.S01E01-E06.1080p.WEB-DL", sizeGb: 6) }, job);

        Assert.True(result.IsEmpty);
        Assert.Contains(result.Notes, n => n.Contains("covers E1-E6") && n.Contains("need E1-E13"));
    }

    [Fact]
    public void Accepts_a_partial_pack_that_does_cover_what_is_missing()
    {
        // Only episodes 2 and 3 are missing, and the pack spans 1-6, so it genuinely covers them.
        var job = TestData.Job(seasonTargets: new() { TestData.Season(1, 2, 3) });
        var result = Plan(new[] { TestData.Release("Severance.S01E01-E06.1080p.WEB-DL", sizeGb: 6) }, job);

        Assert.Equal(DownloadPlanKind.SeasonPack, result.Plan.Kind);
        Assert.Single(result.Plan.Items);
        Assert.True(result.Plan.CoversAllTargets);
    }

    [Fact]
    public void Rejects_non_contiguous_combined_episodes_that_plex_cannot_name_safely()
    {
        var job = TestData.Job(seasonTargets: new() { TestData.Season(1, 1, 3) });
        var result = Plan(new[] { TestData.Release("Severance.S01E01E03.1080p.WEB-DL") }, job);

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Falls_back_to_episodes_when_no_pack_exists()
    {
        var job = TestData.Job(seasonTargets: new() { TestData.Season(2, 1, 2) });
        var result = Plan(new[]
        {
            TestData.Release("Severance.S02E01.1080p.WEB-DL"),
            TestData.Release("Severance.S02E02.1080p.WEB-DL"),
        }, job);

        Assert.Equal(DownloadPlanKind.Episodes, result.Plan.Kind);
        Assert.Equal(2, result.Plan.Items.Count);
        Assert.True(result.Plan.CoversAllTargets);
    }

    [Fact]
    public void Absolute_release_is_planned_against_its_canonical_episode()
    {
        var job = TestData.Job(title: "Severance", episodes: [new() { Season = 2, Episode = 1 }]);
        job.EpisodeOrderProfile = new SeriesEpisodeOrderProfileDto
        {
            TmdbId = 95396,
            SourceOrder = EpisodeOrderType.Absolute,
            MappingsText = "A13 -> S02E01"
        };

        var result = Plan([TestData.Release("Severance - 13 1080p WEB-DL")], job);

        var item = Assert.Single(result.Plan.Items);
        Assert.Equal(2, item.Season);
        Assert.Equal(1, item.Episode);
    }

    [Fact]
    public void Absolute_release_without_an_explicit_mapping_is_rejected()
    {
        var job = TestData.Job(title: "Severance", episodes: [new() { Season = 2, Episode = 2 }]);
        job.EpisodeOrderProfile = new SeriesEpisodeOrderProfileDto
        {
            TmdbId = 95396,
            SourceOrder = EpisodeOrderType.Absolute,
            MappingsText = "A13 -> S02E01"
        };

        var ranked = _eval.Evaluate(TestData.Release("Severance - 14 1080p WEB-DL"), job, TestData.Context());

        Assert.False(ranked.Accepted);
        Assert.Contains(ranked.Rejections, x => x.Reason == RejectionReason.EpisodeMappingMissing);
    }

    [Fact]
    public void A_complete_series_pack_satisfies_any_requested_season()
    {
        var job = TestData.Job(seasonTargets: new() { TestData.Season(3, 1, 2) });
        var result = Plan(new[] { TestData.Release("Severance.COMPLETE.SERIES.1080p.WEB-DL", sizeGb: 40) }, job);
        Assert.False(result.IsEmpty);
    }

    [Fact]
    public void A_release_that_merely_failed_to_parse_a_season_matches_nothing()
    {
        // The distinction that stops an unrelated release being used for an arbitrary season.
        var job = TestData.Job(seasonTargets: new() { TestData.Season(3, 1, 2) });
        var result = Plan(new[] { TestData.Release("Severance.1080p.WEB-DL", sizeGb: 4) }, job);
        Assert.True(result.IsEmpty);
    }

    // REGRESSION: Auto was implemented identically to PreferPack, so the setting did nothing at all.
    [Fact]
    public void Auto_fans_out_when_the_pack_is_poor_value_per_episode()
    {
        var prefs = TestData.Preferences();
        prefs.SeasonPackStrategy = SeasonPackStrategy.Auto;
        var context = TestData.Context(prefs: prefs);

        var job = TestData.Job(seasonTargets: new() { TestData.Season(2, 1, 2, 3) });
        // 60 GB for 3 episodes = 20 GB/episode, versus 2 GB standalone.
        var result = Plan(new[]
        {
            TestData.Release("Severance.S02.1080p.WEB-DL", sizeGb: 60),
            TestData.Release("Severance.S02E01.1080p.WEB-DL", sizeGb: 2),
            TestData.Release("Severance.S02E02.1080p.WEB-DL", sizeGb: 2),
            TestData.Release("Severance.S02E03.1080p.WEB-DL", sizeGb: 2),
        }, job, context);

        Assert.Equal(DownloadPlanKind.Episodes, result.Plan.Kind);
        Assert.Contains(result.Notes, n => n.Contains("fanning out"));
    }

    [Fact]
    public void Auto_takes_the_pack_when_it_is_comparably_priced()
    {
        var prefs = TestData.Preferences();
        prefs.SeasonPackStrategy = SeasonPackStrategy.Auto;
        var context = TestData.Context(prefs: prefs);

        var job = TestData.Job(seasonTargets: new() { TestData.Season(2, 1, 2, 3) });
        var result = Plan(new[]
        {
            TestData.Release("Severance.S02.1080p.WEB-DL", sizeGb: 6),   // 2 GB/episode
            TestData.Release("Severance.S02E01.1080p.WEB-DL", sizeGb: 2),
            TestData.Release("Severance.S02E02.1080p.WEB-DL", sizeGb: 2),
            TestData.Release("Severance.S02E03.1080p.WEB-DL", sizeGb: 2),
        }, job, context);

        Assert.Equal(DownloadPlanKind.SeasonPack, result.Plan.Kind);
    }

    [Fact]
    public void Uses_a_pack_trimmed_to_the_gaps_when_some_episodes_have_no_standalone_release()
    {
        var job = TestData.Job(episodes: new()
        {
            new EpisodeRef { Season = 1, Episode = 1 },
            new EpisodeRef { Season = 1, Episode = 2 },
        });
        var result = Plan(new[]
        {
            TestData.Release("Severance.S01E01.1080p.WEB-DL"),
            TestData.Release("Severance.S01.1080p.WEB-DL", sizeGb: 9),
        }, job);

        Assert.Equal(DownloadPlanKind.Episodes, result.Plan.Kind);
        var pack = result.Plan.Items.Single(i => i.IsPack);
        Assert.Equal(new[] { 2 }, pack.NeededEpisodes);
    }

    [Fact]
    public void An_empty_plan_explains_why_rather_than_going_silent()
    {
        // The old code logged per-candidate reasons at Debug, so in production "why did nothing get picked"
        // had no answer at all.
        var job = TestData.Job(quality: Quality.FullHD);
        var result = Plan(new[]
        {
            TestData.Release("Severance.S02E07.720p.WEB-DL"),
            TestData.Release("Severance.S02E08.720p.WEB-DL"),
        }, job);

        Assert.True(result.IsEmpty);
        var note = Assert.Single(result.Notes);
        Assert.Contains("2 candidate(s) all rejected", note);
        Assert.Contains("below the quality target", note);
    }

    [Fact]
    public void Reports_partial_completion_when_an_episode_has_no_release_at_all()
    {
        var job = TestData.Job(seasonTargets: new() { TestData.Season(2, 1, 2, 3) });
        var result = Plan(new[]
        {
            TestData.Release("Severance.S02E01.1080p.WEB-DL"),
            TestData.Release("Severance.S02E02.1080p.WEB-DL"),
        }, job);

        Assert.Equal(DownloadPlanKind.Episodes, result.Plan.Kind);
        Assert.Contains(result.Notes, n => n.Contains("complete partially"));
        Assert.False(result.Plan.CoversAllTargets);
    }
}
