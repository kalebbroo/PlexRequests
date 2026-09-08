using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class DurableJobRecoveryTests
{
    [Fact]
    public void Durable_transfer_rebuilds_the_complete_cross_season_monitor_contract()
    {
        var job = TestData.Job("Monogatari", MediaType.TvShow, seasonTargets:
        [
            new SeasonTarget { Season = 0, MissingEpisodes = [2, 3] },
            new SeasonTarget { Season = 4, MissingEpisodes = [1] }
        ]);
        var record = FulfillmentWorker.BuildDurableRecoveryRecord(job,
        [
            new TrackedTransferDto
            {
                FulfillmentJobId = job.Id,
                TransferId = "archive-hash",
                IsPack = true,
                SourceSeason = 13,
                NeededEpisodeRefs =
                [
                    new EpisodeRef { Season = 0, Episode = 2 },
                    new EpisodeRef { Season = 0, Episode = 3 },
                    new EpisodeRef { Season = 4, Episode = 1 }
                ]
            }
        ]);

        Assert.True(record.CoversAllTargets);
        var transfer = Assert.Single(record.Transfers);
        Assert.Equal("archive-hash", transfer.TransferId);
        Assert.Equal(13, transfer.SourceSeason);
        Assert.Equal(3, transfer.NeededEpisodeRefs!.Count);
        Assert.False(transfer.Imported);
    }

    [Fact]
    public void Durable_recovery_retains_partial_plan_semantics()
    {
        var job = TestData.Job("Anime", MediaType.TvShow, seasonTargets:
        [
            new SeasonTarget { Season = 0, MissingEpisodes = [2, 3] }
        ]);
        var record = FulfillmentWorker.BuildDurableRecoveryRecord(job,
        [
            new TrackedTransferDto
            {
                FulfillmentJobId = job.Id,
                TransferId = "partial-archive",
                IsPack = true,
                NeededEpisodeRefs = [new EpisodeRef { Season = 0, Episode = 2 }]
            }
        ]);

        Assert.False(record.CoversAllTargets);
    }

    [Fact]
    public void Durable_single_episode_identity_is_preserved()
    {
        var job = TestData.Job("Kids Show", MediaType.TvShow,
            episodes: [new EpisodeRef { Season = 2, Episode = 7 }]);
        var record = FulfillmentWorker.BuildDurableRecoveryRecord(job,
        [
            new TrackedTransferDto
            {
                FulfillmentJobId = job.Id,
                TransferId = "episode-transfer",
                Season = 2,
                Episode = 7,
                IsPack = false
            }
        ]);

        Assert.True(record.CoversAllTargets);
        Assert.Equal((2, 7), (record.Transfers[0].Season, record.Transfers[0].Episode));
    }
}
