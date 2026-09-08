using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class ImportAuditCoverageTests
{
    [Fact]
    public void Reused_payload_id_does_not_satisfy_a_different_episode_scope()
    {
        var audit = new[]
        {
            Imported("same-hash", 1, 1),
            Imported("same-hash", 1, 2)
        };
        var wanted = new[]
        {
            new EpisodeRef { Season = 0, Episode = 2 },
            new EpisodeRef { Season = 4, Episode = 1 }
        };

        Assert.False(ImportAuditCoverage.Covers(AcquisitionProtocol.Torrent, "same-hash",
            wanted, season: null, episode: null, neededEpisodes: null, audit));
    }

    [Fact]
    public void Audit_must_cover_every_current_canonical_target()
    {
        var wanted = new[]
        {
            new EpisodeRef { Season = 0, Episode = 2 },
            new EpisodeRef { Season = 4, Episode = 1 }
        };
        var incomplete = new[] { Imported("same-hash", 0, 2) };
        var complete = incomplete.Append(Imported("same-hash", 4, 1)).ToList();

        Assert.False(ImportAuditCoverage.Covers(AcquisitionProtocol.Torrent, "same-hash",
            wanted, season: null, episode: null, neededEpisodes: null, incomplete));
        Assert.True(ImportAuditCoverage.Covers(AcquisitionProtocol.Torrent, "same-hash",
            wanted, season: null, episode: null, neededEpisodes: null, complete));
    }

    [Fact]
    public void Subtitle_alone_never_proves_an_import()
    {
        var audit = new[]
        {
            Imported("same-hash", 4, 1, "subtitle")
        };

        Assert.False(ImportAuditCoverage.Covers(AcquisitionProtocol.Torrent, "same-hash",
            neededEpisodeRefs: null, season: 4, episode: 1, neededEpisodes: null, audit));
    }

    private static ImportedFileDto Imported(string transferId, int season, int episode,
        string fileType = "video") => new()
    {
        TransferId = transferId,
        Protocol = AcquisitionProtocol.Torrent,
        FileType = fileType,
        SeasonNumber = season,
        EpisodeNumber = episode,
        EpisodeCoverage = [new EpisodeRef { Season = season, Episode = episode }]
    };
}
