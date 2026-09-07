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

    /// <summary>Assigns a profile to a series without allowing mappings from the previous identity to leak.</summary>
    public static void AssignSeries(SeriesEpisodeOrderProfileDto profile, int tmdbId, string? seriesTitle = null)
    {
        if (profile.TmdbId == tmdbId)
        {
            if (!string.IsNullOrWhiteSpace(seriesTitle)) profile.SeriesTitle = seriesTitle.Trim();
            return;
        }

        profile.TmdbId = tmdbId;
        profile.SeriesTitle = !string.IsNullOrWhiteSpace(seriesTitle)
            ? seriesTitle.Trim()
            : tmdbId > 0 ? $"TMDb {tmdbId}" : string.Empty;
        profile.SourceEpisodeGroupId = null;
        profile.SourceEpisodeGroupName = null;
        profile.ImportedAt = null;
        profile.SourceGroups = [];
        profile.MappingsText = string.Empty;
    }

    public static bool TryParse(SeriesEpisodeOrderProfileDto profile,
        out IReadOnlyDictionary<(int Season, int Episode), EpisodeRef> mappings, out string? error)
    {
        var result = new Dictionary<(int, int), EpisodeRef>();
        var targets = new HashSet<(int, int)>();
        var sourceGroupSeasons = new HashSet<int>();
        if (!string.IsNullOrWhiteSpace(profile.SourceEpisodeGroupId)
            && profile.SourceOrder != EpisodeOrderType.Absolute
            && (profile.SourceGroups?.Count ?? 0) == 0)
        {
            mappings = result;
            error = "This imported episode group predates folder-title verification; re-import it from TMDb before use.";
            return false;
        }
        foreach (var group in profile.SourceGroups ?? [])
        {
            if (group.SourceSeason <= 0 || string.IsNullOrWhiteSpace(group.Name) || group.Name.Trim().Length > 256)
            {
                mappings = result;
                error = "Imported source groups must have a positive number and a non-empty name.";
                return false;
            }
            if (!sourceGroupSeasons.Add(group.SourceSeason))
            {
                mappings = result;
                error = $"Imported source group S{group.SourceSeason:D2} is repeated.";
                return false;
            }
        }
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

        if (sourceGroupSeasons.Count > 0 && result.Keys.Any(key => !sourceGroupSeasons.Contains(key.Item1)))
        {
            var unknown = result.Keys.First(key => !sourceGroupSeasons.Contains(key.Item1));
            mappings = result;
            error = $"Source mapping {SourceLabel(unknown.Item1, unknown.Item2)} has no matching imported group name.";
            return false;
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

    /// <summary>
    /// Translate an episode identity using both its filename and, when needed, a numbered parent folder.
    /// Anime franchise batches commonly contain paths such as
    /// <c>01 - Bakemonogatari/[Group] Bakemonogatari - 01.mkv</c>: the filename is absolute-style, while
    /// the parent folder is the source season/story-arc used by a TMDb episode group. We accept the folder
    /// only when it resolves through the explicit immutable mapping, and fail if filename and folder
    /// evidence point at different canonical episodes.
    /// </summary>
    public static bool TryTranslateFile(SeriesEpisodeOrderProfileDto? profile, string filePath,
        int parsedSeason, int sourceEpisode, out EpisodeRef target)
    {
        if (!IsActive(profile))
            return TryTranslate(profile, parsedSeason, sourceEpisode, out target);
        if (!TryParse(profile!, out var map, out _))
        {
            target = new EpisodeRef();
            return false;
        }

        var sourceKeys = new List<(int Season, int Episode)> { (parsedSeason, sourceEpisode) };
        if (TryNumberedParent(filePath, out var parentSeason, out var parentName))
        {
            var authoritativeGroups = profile!.SourceGroups ?? [];
            if (authoritativeGroups.Count > 0)
            {
                if (!SourceGroupNameMatches(authoritativeGroups, parentSeason, parentName))
                {
                    target = new EpisodeRef();
                    return false;
                }
                // A proven collection folder outranks an arc's own S1/S2 token. Mixing both identities can
                // accidentally map an embedded sub-series through the first franchise group.
                sourceKeys = [(parentSeason, sourceEpisode)];
            }
            else if (parsedSeason == 0)
                sourceKeys.Insert(0, (parentSeason, sourceEpisode));
        }

        var matches = sourceKeys.Distinct()
            .Where(map.ContainsKey)
            .Select(key => map[key])
            .DistinctBy(episode => (episode.Season, episode.Episode))
            .ToList();
        if (matches.Count == 1)
        {
            target = new EpisodeRef { Season = matches[0].Season, Episode = matches[0].Episode };
            return true;
        }

        target = new EpisodeRef();
        return false;
    }

    /// <summary>Resolves Plex's canonical aired numbering back to the numbering used in release names.</summary>
    public static bool TryTranslateCanonicalToSource(SeriesEpisodeOrderProfileDto? profile,
        int canonicalSeason, int canonicalEpisode, out EpisodeRef source)
    {
        if (!IsActive(profile))
        {
            source = new EpisodeRef { Season = canonicalSeason, Episode = canonicalEpisode };
            return true;
        }
        if (TryParse(profile!, out var map, out _))
        {
            foreach (var pair in map)
            {
                if (pair.Value.Season != canonicalSeason || pair.Value.Episode != canonicalEpisode) continue;
                source = new EpisodeRef { Season = pair.Key.Season, Episode = pair.Key.Episode };
                return true;
            }
        }
        source = new EpisodeRef();
        return false;
    }

    /// <summary>Returns release-numbered episodes whose canonical targets belong to the requested seasons.</summary>
    public static IReadOnlyList<EpisodeRef> SourcesForCanonicalSeasons(
        SeriesEpisodeOrderProfileDto profile, IEnumerable<int> canonicalSeasons)
    {
        if (!TryParse(profile, out var map, out _)) return [];
        var wanted = canonicalSeasons.ToHashSet();
        return map.Where(x => wanted.Contains(x.Value.Season))
            .Select(x => new EpisodeRef { Season = x.Key.Season, Episode = x.Key.Episode })
            .OrderBy(x => x.Season).ThenBy(x => x.Episode).ToList();
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

    private static bool TryNumberedParent(string filePath, out int season, out string name)
    {
        season = 0;
        name = string.Empty;
        var parent = Path.GetFileName(Path.GetDirectoryName(filePath));
        if (string.IsNullOrWhiteSpace(parent)) return false;
        var match = NumberedParentRegex().Match(parent);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out season) || season <= 0) return false;
        name = match.Groups[2].Value.Trim();
        return name.Length > 0;
    }

    private static bool SourceGroupNameMatches(IReadOnlyCollection<EpisodeOrderSourceGroupDto> groups,
        int sourceSeason, string folderName)
    {
        var expected = groups.FirstOrDefault(group => group.SourceSeason == sourceSeason);
        if (expected is null) return false;
        var folderTokens = TitleTokens(folderName);
        var expectedTokens = TitleTokens(expected.Name);
        if (folderTokens.Count == 0 || expectedTokens.Count == 0) return false;
        if (folderTokens.SequenceEqual(expectedTokens)) return true;

        // Release folders commonly split one named arc into season/part/cour folders. Permit only those
        // explicit structural suffixes; arbitrary extra title words must not turn one anime into another.
        return folderTokens.Count > expectedTokens.Count
               && folderTokens.Take(expectedTokens.Count).SequenceEqual(expectedTokens)
               && folderTokens.Skip(expectedTokens.Count).All(IsStructuralSuffix);
    }

    private static List<string> TitleTokens(string value) => TitleTokenRegex().Matches(value)
        .Select(match => match.Value.ToLowerInvariant()).ToList();

    private static bool IsStructuralSuffix(string token) =>
        token is "season" or "part" or "cour" or "arc"
        || StructuralNumberRegex().IsMatch(token);

    [GeneratedRegex(@"^(?:(?:S(\d{1,3})E)|A)(\d{1,4})\s*(?:->|=)\s*S(\d{1,3})E(\d{1,4})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MappingLineRegex();

    [GeneratedRegex(@"^\s*(\d{1,3})(?:\s*[-_.:]\s*|\s+)(.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedParentRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex TitleTokenRegex();

    [GeneratedRegex(@"^(?:s|season|part|pt|cour)?\d{1,3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StructuralNumberRegex();
}
