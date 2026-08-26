using System.Text.RegularExpressions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared;

/// <summary>Parses and applies an administrator's explicit source-number to Plex-number contract.</summary>
public static partial class EpisodeOrderMapping
{
    public static bool IsActive(SeriesEpisodeOrderProfileDto? profile) =>
        profile is { Enabled: true, SourceOrder: not EpisodeOrderType.Aired };

    public static SeriesEpisodeOrderProfileDto? Resolve(
        IEnumerable<SeriesEpisodeOrderProfileDto> profiles, int? tmdbId) =>
        tmdbId is > 0
            ? profiles.FirstOrDefault(x => x.Enabled && x.TmdbId == tmdbId)
            : null;

    public static bool TryParse(SeriesEpisodeOrderProfileDto profile,
        out IReadOnlyDictionary<(int Season, int Episode), EpisodeRef> mappings, out string? error)
    {
        var result = new Dictionary<(int, int), EpisodeRef>();
        var targets = new HashSet<(int, int)>();
        var lines = (profile.MappingsText ?? string.Empty).Replace("\r", string.Empty).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var match = MappingLineRegex().Match(line);
            if (!match.Success)
            {
                mappings = result;
                error = $"Line {i + 1} must look like S01E13 -> S02E01 or A13 -> S02E01.";
                return false;
            }

            var sourceSeason = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 0;
            var sourceEpisode = int.Parse(match.Groups[2].Value);
            var targetSeason = int.Parse(match.Groups[3].Value);
            var targetEpisode = int.Parse(match.Groups[4].Value);
            if (sourceEpisode <= 0 || targetSeason < 0 || targetEpisode <= 0)
            {
                mappings = result;
                error = $"Line {i + 1} contains an invalid season or episode number.";
                return false;
            }
            if (!result.TryAdd((sourceSeason, sourceEpisode),
                    new EpisodeRef { Season = targetSeason, Episode = targetEpisode }))
            {
                mappings = result;
                error = $"Line {i + 1} repeats source episode {SourceLabel(sourceSeason, sourceEpisode)}.";
                return false;
            }
            if (!targets.Add((targetSeason, targetEpisode)))
            {
                mappings = result;
                error = $"Line {i + 1} maps more than one source episode to S{targetSeason:D2}E{targetEpisode:D2}.";
                return false;
            }
        }

        mappings = result;
        error = result.Count == 0 ? "At least one episode mapping is required." : null;
        return error is null;
    }

    public static bool TryTranslate(SeriesEpisodeOrderProfileDto? profile, int sourceSeason, int sourceEpisode,
        out EpisodeRef target)
    {
        if (!IsActive(profile))
        {
            target = new EpisodeRef { Season = sourceSeason, Episode = sourceEpisode };
            return true;
        }
        if (TryParse(profile!, out var map, out _) && map.TryGetValue((sourceSeason, sourceEpisode), out var found))
        {
            target = new EpisodeRef { Season = found.Season, Episode = found.Episode };
            return true;
        }
        target = new EpisodeRef();
        return false;
    }

    public static IReadOnlyList<EpisodeRef> SourceSeasonCoverage(SeriesEpisodeOrderProfileDto profile, int? sourceSeason)
    {
        if (!TryParse(profile, out var map, out _)) return [];
        return map.Where(x => sourceSeason is null || x.Key.Season == sourceSeason)
            .Select(x => new EpisodeRef { Season = x.Value.Season, Episode = x.Value.Episode })
            .OrderBy(x => x.Season).ThenBy(x => x.Episode).ToList();
    }

    public static string Normalize(SeriesEpisodeOrderProfileDto profile)
    {
        if (!TryParse(profile, out var map, out _)) return profile.MappingsText.Trim();
        return string.Join('\n', map.OrderBy(x => x.Key.Season).ThenBy(x => x.Key.Episode)
            .Select(x => $"{SourceLabel(x.Key.Season, x.Key.Episode)} -> S{x.Value.Season:D2}E{x.Value.Episode:D2}"));
    }

    private static string SourceLabel(int season, int episode) => season == 0 ? $"A{episode}" : $"S{season:D2}E{episode:D2}";

    [GeneratedRegex(@"^(?:(?:S(\d{1,3})E)|A)(\d{1,4})\s*(?:->|=)\s*S(\d{1,3})E(\d{1,4})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MappingLineRegex();
}
