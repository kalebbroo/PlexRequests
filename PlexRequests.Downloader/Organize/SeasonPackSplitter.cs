using PlexRequests.Downloader.Ranking;

namespace PlexRequests.Downloader.Organize;

/// <summary>
/// Maps each video file inside a season-pack download to the episode number it represents, so the
/// organizer can rename/place them individually instead of dumping the pack's raw internal names.
/// </summary>
public interface ISeasonPackSplitter
{
    /// <summary>
    /// Files that can't be confidently mapped are simply omitted from the result (never guessed) — the
    /// caller should log each omission loudly rather than importing it under a wrong episode number.
    /// </summary>
    IReadOnlyList<(string FilePath, int Episode)> Map(IReadOnlyList<string> videoFiles, int season, int? expectedEpisodeCount);
}

public class SeasonPackSplitter(IReleaseParser parser, ILogger<SeasonPackSplitter> logger) : ISeasonPackSplitter
{
    public IReadOnlyList<(string FilePath, int Episode)> Map(IReadOnlyList<string> videoFiles, int season, int? expectedEpisodeCount)
    {
        var result = new List<(string, int)>();
        var unmapped = new List<string>();

        // Primary: per-filename SxxExx parse — the overwhelming majority of real releases keep this in
        // every internal filename, even when the outer pack name doesn't.
        foreach (var file in videoFiles)
        {
            var parsed = parser.Parse(Path.GetFileName(file));
            if (parsed.Episode is int ep && (parsed.Season is null || parsed.Season == season))
                result.Add((file, ep));
            else
                unmapped.Add(file);
        }

        // Fallback: only when EVERY file failed to parse (never a partial mix — that's how a confident
        // parse and a guess end up silently disagreeing) AND the count matches what TMDB expects for the
        // season, sort naturally and assign 1..N in order.
        if (result.Count == 0 && unmapped.Count > 0 && expectedEpisodeCount is int expected && unmapped.Count == expected)
        {
            var ordered = unmapped.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < ordered.Count; i++) result.Add((ordered[i], i + 1));
            logger.LogInformation("Season pack S{Season}: no per-file episode numbers found; assigned {Count} file(s) by alphabetical order", season, ordered.Count);
            return result;
        }

        foreach (var file in unmapped)
            logger.LogWarning("Season pack S{Season}: could not confidently map \"{File}\" to an episode; skipped", season, Path.GetFileName(file));

        return result;
    }
}
