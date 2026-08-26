using PlexRequests.Downloader.Ranking;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Organize;

/// <summary>
/// Maps each video file inside a season-pack download to the episode number it represents, so the
/// organizer can rename/place them individually instead of dumping the pack's raw internal names.
/// </summary>
public interface ISeasonPackSplitter
{
    /// <summary>
    /// Files that can't be confidently mapped are returned explicitly (never guessed) so the caller can
    /// reject the complete import before any library write.
    /// </summary>
    SeasonPackMapResult Map(IReadOnlyList<string> videoFiles, int season, int? expectedEpisodeCount);
}

/// <summary>One physical file and every logical episode it explicitly says it covers.</summary>
public sealed record EpisodeFileMapping(string FilePath, int Season, IReadOnlyList<int> Episodes);

/// <summary>Complete mapping result. Unmapped files and overlapping coverage are first-class because the
/// organizer must fail closed rather than silently omit or guess at an episode.</summary>
public sealed record SeasonPackMapResult(
    IReadOnlyList<EpisodeFileMapping> Mappings,
    IReadOnlyList<string> UnmappedFiles,
    IReadOnlyList<int> ConflictingEpisodes)
{
    public bool IsUnambiguous => UnmappedFiles.Count == 0 && ConflictingEpisodes.Count == 0;
}

public class SeasonPackSplitter(IReleaseParser parser, ILogger<SeasonPackSplitter> logger) : ISeasonPackSplitter
{
    public SeasonPackMapResult Map(IReadOnlyList<string> videoFiles, int season, int? expectedEpisodeCount)
    {
        var result = new List<EpisodeFileMapping>();
        var unmapped = new List<string>();

        // Primary: per-filename SxxExx parse — the overwhelming majority of real releases keep this in
        // every internal filename, even when the outer pack name doesn't.
        foreach (var file in videoFiles)
        {
            var parsed = parser.Parse(Path.GetFileName(file));
            var episodes = parsed.EpisodeNumbers.Distinct().OrderBy(x => x).ToList();
            var seasonMatches = parsed.Season is null || parsed.Season == season;
            var episodesValid = episodes.Count > 0 && episodes.All(x => x > 0)
                && episodes.SequenceEqual(Enumerable.Range(episodes[0], episodes.Count))
                && (expectedEpisodeCount is not int expected || episodes.All(x => x <= expected));
            if (seasonMatches && episodesValid)
                result.Add(new EpisodeFileMapping(file, season, episodes));
            else
                unmapped.Add(file);
        }

        foreach (var file in unmapped)
            logger.LogWarning("Season pack S{Season}: could not confidently map \"{File}\" to an episode", season, Path.GetFileName(file));

        var conflicts = result.SelectMany(x => x.Episodes)
            .GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(x => x).ToList();
        if (conflicts.Count > 0)
            logger.LogWarning("Season pack S{Season}: multiple files claim episode(s) {Episodes}",
                season, string.Join(",", conflicts));

        return new SeasonPackMapResult(result, unmapped, conflicts);
    }
}
