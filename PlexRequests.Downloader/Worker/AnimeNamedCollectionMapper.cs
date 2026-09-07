using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Worker;

/// <summary>
/// Maps one file inside a multi-season anime collection by its distinctive canonical season name. This is
/// intentionally narrower than trusting a folder ordinal: both the series identity and exactly one season
/// name must survive into the path, and the filename must still carry explicit contiguous episode numbers.
/// </summary>
internal static class AnimeNamedCollectionMapper
{
    public static bool TryMapFile(
        string path,
        FulfillmentJobDto job,
        ParsedRelease parsed,
        out IReadOnlyList<EpisodeRef> coverage)
    {
        coverage = [];
        if (job.CanonicalSeasons.Count == 0 || parsed.FractionalEpisodeNumber
            || parsed.Season is not int sourceSeason)
            return false;

        var episodes = parsed.EpisodeNumbers.Distinct().Order().ToList();
        if (episodes.Count == 0 || !episodes.SequenceEqual(Enumerable.Range(episodes[0], episodes.Count)))
            return false;

        var match = AnimeSeasonIdentity.Match(path, job.Title, job.CanonicalSeasons, sourceSeason);
        if (match is null) return false;

        coverage = episodes.Select(episode => new EpisodeRef
        {
            Season = match.CanonicalSeason,
            Episode = episode
        }).ToList();
        return true;
    }
}
