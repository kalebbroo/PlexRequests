using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Worker;

internal sealed record NamedSeasonSequenceMap(
    int InsertionAfter,
    IReadOnlyDictionary<string, int> CanonicalEpisodes);

/// <summary>
/// Maps the narrow anime convention where a complete, distinctively named season contains one inserted
/// half episode (for example 01..06, 06.5, 07..14) while Plex numbers the same files 01..15. The mapping
/// is intentionally unavailable for standalone files and incomplete/gapped manifests: only the complete
/// manifest can prove that the fraction is an insertion rather than an unrelated special.
/// </summary>
internal static class AnimeNamedSeasonSequenceMapper
{
    public static bool TryDetect(
        IReadOnlyList<string> videoPaths,
        FulfillmentJobDto job,
        int sourceSeason,
        int canonicalSeason,
        int expectedEpisodeCount,
        IReleaseParser parser,
        out NamedSeasonSequenceMap? map,
        out string? failure)
    {
        map = null;
        failure = null;
        if (expectedEpisodeCount < 3) return false;

        var entries = new List<(string Path, decimal Ordinal)>();
        foreach (var path in videoPaths)
        {
            if (!AnimeManifestPreflight.MatchesNamedCanonicalSeason(
                    job, path, sourceSeason, canonicalSeason))
                continue;

            var parsed = parser.Parse(Path.GetFileName(path));
            if (TryGetSourceOrdinal(parsed, sourceSeason, out var ordinal))
                entries.Add((path, ordinal));
        }

        var fractions = entries.Where(entry => entry.Ordinal != decimal.Truncate(entry.Ordinal)).ToList();
        if (fractions.Count == 0) return false;

        if (fractions.Count != 1)
        {
            failure = $"Named season sequence contains {fractions.Count} fractional episodes; only one complete, unambiguous insertion can be mapped automatically.";
            return true;
        }

        var fractional = fractions[0].Ordinal;
        var insertionAfter = decimal.ToInt32(decimal.Truncate(fractional));
        if (fractional != insertionAfter + 0.5m)
        {
            failure = $"Fractional episode {fractional} is not the supported N.5 insertion form; explicit episode-order mapping or admin review is required.";
            return true;
        }

        var lastSourceEpisode = expectedEpisodeCount - 1;
        if (insertionAfter < 1 || insertionAfter >= lastSourceEpisode)
        {
            failure = $"Fractional episode {fractional} is not between two episodes in the complete 1-{lastSourceEpisode} source sequence.";
            return true;
        }

        var ordinals = entries.Select(entry => entry.Ordinal).ToList();
        if (ordinals.Distinct().Count() != ordinals.Count)
        {
            failure = "Named season sequence contains duplicate episode numbers; no canonical insertion can be proven.";
            return true;
        }

        var integers = ordinals.Where(value => value == decimal.Truncate(value))
            .Select(decimal.ToInt32).Order().ToList();
        var expectedIntegers = Enumerable.Range(1, lastSourceEpisode).ToList();
        if (entries.Count != expectedEpisodeCount || !integers.SequenceEqual(expectedIntegers))
        {
            failure = $"Fractional named season cannot prove a complete sequence: expected integer episodes 1-{lastSourceEpisode} plus {fractional} ({expectedEpisodeCount} files total).";
            return true;
        }

        var mappings = entries.ToDictionary(
            entry => entry.Path,
            entry => ToCanonicalEpisode(entry.Ordinal, insertionAfter),
            StringComparer.Ordinal);
        map = new NamedSeasonSequenceMap(insertionAfter, mappings);
        return true;
    }

    public static bool TryMapFile(
        string path,
        FulfillmentJobDto job,
        int sourceSeason,
        int canonicalSeason,
        int expectedEpisodeCount,
        int insertionAfter,
        IReleaseParser parser,
        out int canonicalEpisode)
    {
        canonicalEpisode = 0;
        if (insertionAfter < 1 || insertionAfter >= expectedEpisodeCount - 1
            || !AnimeManifestPreflight.MatchesNamedCanonicalSeason(
                job, path, sourceSeason, canonicalSeason))
            return false;

        var parsed = parser.Parse(Path.GetFileName(path));
        if (!TryGetSourceOrdinal(parsed, sourceSeason, out var ordinal)) return false;
        var isInteger = ordinal == decimal.Truncate(ordinal);
        if (!isInteger && ordinal != insertionAfter + 0.5m) return false;

        canonicalEpisode = ToCanonicalEpisode(ordinal, insertionAfter);
        return canonicalEpisode > 0 && canonicalEpisode <= expectedEpisodeCount;
    }

    private static bool TryGetSourceOrdinal(ParsedRelease parsed, int sourceSeason, out decimal ordinal)
    {
        if (parsed.FractionalEpisode is decimal fractional
            && parsed.FractionalEpisodeSeason is int fractionalSeason
            && (fractionalSeason == 0 || fractionalSeason == sourceSeason))
        {
            ordinal = fractional;
            return ordinal > 0;
        }

        var episodes = parsed.EpisodeNumbers.Distinct().ToList();
        if (episodes.Count == 1 && parsed.Season is int season
            && (season == 0 || season == sourceSeason))
        {
            ordinal = episodes[0];
            return ordinal > 0;
        }

        ordinal = 0;
        return false;
    }

    private static int ToCanonicalEpisode(decimal sourceOrdinal, int insertionAfter)
    {
        if (sourceOrdinal != decimal.Truncate(sourceOrdinal)) return insertionAfter + 1;
        var sourceEpisode = decimal.ToInt32(sourceOrdinal);
        return sourceEpisode <= insertionAfter ? sourceEpisode : sourceEpisode + 1;
    }
}
