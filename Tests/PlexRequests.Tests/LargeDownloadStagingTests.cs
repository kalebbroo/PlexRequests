using PlexRequests.Downloader.Download;
using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

public sealed class LargeDownloadStagingTests
{
    private readonly ReleaseParser _parser = new();

    [Fact]
    public void Seasons_are_staged_in_order_only_while_they_fit()
    {
        var seasons = new (int Season, long Bytes)[] { (1, 30), (2, 30), (3, 30), (4, 30) };

        var chosen = FulfillmentPipeline.FittingSeasonPrefix(seasons, next => next <= 65);

        Assert.Equal([1, 2], chosen);
    }

    [Fact]
    public void Nothing_is_staged_when_the_first_season_does_not_fit()
    {
        var chosen = FulfillmentPipeline.FittingSeasonPrefix([(1, 30)], next => next <= 10);

        Assert.Empty(chosen);
    }

    [Fact]
    public void Only_the_files_of_the_chosen_seasons_are_selected_from_a_multi_season_torrent()
    {
        var files = new[]
        {
            new AcquisitionManifestFile("From/Season 01/From.S01E01.mkv", 1),
            new AcquisitionManifestFile("From/Season 02/From.S02E01.mkv", 1),
            new AcquisitionManifestFile("From/Season 02/From.S02E01.srt", 1),
            new AcquisitionManifestFile("From/Season 03/From.S03E01.mkv", 1),
        };

        var keep = FulfillmentPipeline.KeepFilesForSeasons(files, _parser, fallbackSeason: null, seasons: [2]);

        Assert.Equal([false, true, true, false], keep);
    }

    [Fact]
    public void A_file_with_no_readable_season_takes_the_items_season_when_it_has_one()
    {
        var files = new[] { new AcquisitionManifestFile("From/extras/Featurette.mkv", 1) };

        Assert.Equal([true], FulfillmentPipeline.KeepFilesForSeasons(files, _parser, fallbackSeason: 3, seasons: [3]));
        Assert.Equal([false], FulfillmentPipeline.KeepFilesForSeasons(files, _parser, fallbackSeason: null, seasons: [3]));
    }

    [Fact]
    public void Pending_targets_come_from_the_season_targets_still_missing_episodes()
    {
        var job = new FulfillmentJobDto
        {
            Id = 1, Title = "From", MediaType = MediaType.TvShow,
            SeasonTargets =
            [
                new SeasonTarget { Season = 1, MissingEpisodes = [] },
                new SeasonTarget { Season = 2, MissingEpisodes = [3, 1] },
            ],
        };

        var pending = FulfillmentPipeline.PendingSeasonTargets(job);

        var season = Assert.Single(pending);
        Assert.Equal(2, season.Season);
        Assert.Equal([1, 3], season.Episodes);
    }

    [Fact]
    public void Pending_targets_fall_back_to_the_requested_episodes_grouped_by_season()
    {
        var job = new FulfillmentJobDto
        {
            Id = 2, Title = "From", MediaType = MediaType.TvShow,
            RequestedEpisodes = [new EpisodeRef { Season = 2, Episode = 2 }, new EpisodeRef { Season = 1, Episode = 1 }],
        };

        var pending = FulfillmentPipeline.PendingSeasonTargets(job);

        Assert.Equal([1, 2], pending.Select(target => target.Season));
    }
}
