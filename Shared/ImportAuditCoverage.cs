using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared;

/// <summary>
/// Proves that a durable import audit satisfies the current transfer intent. A backend id identifies the
/// payload, not the requested slice of that payload: the same season/franchise torrent may be reused later
/// for different episodes, so an id match by itself is never enough when canonical targets are available.
/// </summary>
public static class ImportAuditCoverage
{
    public static bool Covers(AcquisitionProtocol protocol, string transferId,
        IReadOnlyCollection<EpisodeRef>? neededEpisodeRefs, int? season, int? episode,
        IReadOnlyCollection<int>? neededEpisodes, IEnumerable<ImportedFileDto> audit)
    {
        if (string.IsNullOrWhiteSpace(transferId)) return false;
        var matching = audit.Where(file => file.Protocol == protocol
                                           && string.Equals(file.TransferId, transferId,
                                               StringComparison.OrdinalIgnoreCase)
                                           && !string.Equals(file.FileType, "subtitle",
                                               StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matching.Count == 0) return false;

        var expected = ExpectedTargets(neededEpisodeRefs, season, episode, neededEpisodes);
        if (expected.Count == 0) return true;

        var imported = matching.SelectMany(file => file.EpisodeCoverage is { Count: > 0 }
                ? file.EpisodeCoverage.Select(target => (target.Season, target.Episode))
                : file.SeasonNumber is int importedSeason && file.EpisodeNumber is int importedEpisode
                    ? [(Season: importedSeason, Episode: importedEpisode)]
                    : Array.Empty<(int Season, int Episode)>())
            .Where(target => target.Season >= 0 && target.Episode > 0)
            .ToHashSet();
        return expected.IsSubsetOf(imported);
    }

    private static HashSet<(int Season, int Episode)> ExpectedTargets(
        IReadOnlyCollection<EpisodeRef>? neededEpisodeRefs, int? season, int? episode,
        IReadOnlyCollection<int>? neededEpisodes)
    {
        if (neededEpisodeRefs is { Count: > 0 })
            return neededEpisodeRefs.Where(target => target.Season >= 0 && target.Episode > 0)
                .Select(target => (target.Season, target.Episode)).ToHashSet();
        if (season is int packSeason && neededEpisodes is { Count: > 0 })
            return neededEpisodes.Where(number => number > 0)
                .Select(number => (packSeason, number)).ToHashSet();
        return season is int singleSeason && episode is int singleEpisode && singleEpisode > 0
            ? [(singleSeason, singleEpisode)]
            : [];
    }
}
