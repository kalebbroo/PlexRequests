using System.Text.RegularExpressions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared;

/// <summary>Parses and applies an administrator's explicit source-number to Plex-number contract.</summary>
public static partial class EpisodeOrderMapping
{
    public static bool IsActive(SeriesEpisodeOrderProfileDto? profile) =>
        profile is { Enabled: true, SourceOrder: not EpisodeOrderType.Aired };

    public static bool HasCustomMetadata(SeriesEpisodeOrderProfileDto? profile) =>
        IsActive(profile) && profile is { CustomMetadataEnabled: true, CustomEpisodes.Count: > 0 };

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
        profile.CustomMetadataEnabled = false;
        profile.CustomSeasons = [];
        profile.CustomEpisodes = [];
        profile.MappingsText = string.Empty;
    }

    public static bool TryParseDetailed(SeriesEpisodeOrderProfileDto profile,
        out IReadOnlyDictionary<(int Season, int Episode), EpisodeMapTarget> mappings, out string? error)
    {
        if (profile.CustomMetadataEnabled)
            return TryParseCustom(profile, out mappings, out error);

        var legacy = TryParseLegacy(profile, out var parsed, out error);
        mappings = parsed.ToDictionary(pair => pair.Key,
            pair => new EpisodeMapTarget(new EpisodeRef
            {
                Season = pair.Value.Season,
                Episode = pair.Value.Episode
            }));
        return legacy;
    }

    public static bool TryParse(SeriesEpisodeOrderProfileDto profile,
        out IReadOnlyDictionary<(int Season, int Episode), EpisodeRef> mappings, out string? error)
    {
        var valid = TryParseDetailed(profile, out var detailed, out error);
        mappings = detailed.ToDictionary(pair => pair.Key,
            pair => new EpisodeRef
            {
                Season = pair.Value.Episode.Season,
                Episode = pair.Value.Episode.Episode
            });
        return valid;
    }

    private static bool TryParseLegacy(SeriesEpisodeOrderProfileDto profile,
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

    private static bool TryParseCustom(SeriesEpisodeOrderProfileDto profile,
        out IReadOnlyDictionary<(int Season, int Episode), EpisodeMapTarget> mappings, out string? error)
    {
        var result = new Dictionary<(int, int), EpisodeMapTarget>();
        var sourceGroups = new HashSet<int>();
        foreach (var group in profile.SourceGroups ?? [])
        {
            if (group.SourceSeason <= 0 || string.IsNullOrWhiteSpace(group.Name)
                || group.Name.Trim().Length > 256 || !sourceGroups.Add(group.SourceSeason))
            {
                mappings = result;
                error = "Custom source folders need a unique positive number and a name.";
                return false;
            }
        }
        var seasons = new HashSet<int>();
        foreach (var season in profile.CustomSeasons ?? [])
        {
            if (season.Season < 0 || season.Season > 999 || string.IsNullOrWhiteSpace(season.Name)
                || season.Name.Trim().Length > 256)
            {
                mappings = result;
                error = "Custom seasons need a unique non-negative number and a name.";
                return false;
            }
            if (!seasons.Add(season.Season))
            {
                mappings = result;
                error = $"Custom season S{season.Season:D2} is repeated.";
                return false;
            }
        }

        var allowedKinds = new HashSet<string>(["episode", "ova", "ona", "special"],
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in profile.CustomEpisodes ?? [])
        {
            if (row.SourceSeason < 0 || row.SourceSeason > 999 || row.SourceEpisode <= 0
                || row.SourceEpisode > 9999 || row.Season < 0 || row.Season > 999
                || row.Episode <= 0 || row.Episode > 9999 || row.Part is < 1 or > 8)
            {
                mappings = result;
                error = "Custom episode rows need valid source/Plex numbers and a part from 1 through 8.";
                return false;
            }
            if (!allowedKinds.Contains(row.ContentKind?.Trim() ?? string.Empty))
            {
                mappings = result;
                error = $"{SourceLabel(row.SourceSeason, row.SourceEpisode)} has an unsupported content type.";
                return false;
            }
            if (!result.TryAdd((row.SourceSeason, row.SourceEpisode),
                    new EpisodeMapTarget(new EpisodeRef { Season = row.Season, Episode = row.Episode }, row.Part)))
            {
                mappings = result;
                error = $"Custom metadata repeats source episode {SourceLabel(row.SourceSeason, row.SourceEpisode)}.";
                return false;
            }
        }

        foreach (var target in (profile.CustomEpisodes ?? []).GroupBy(row => (row.Season, row.Episode)))
        {
            var parts = target.Select(row => row.Part).Order().ToList();
            if (parts.Count > 1 && !parts.SequenceEqual(Enumerable.Range(1, parts.Count)))
            {
                mappings = result;
                error = $"S{target.Key.Season:D2}E{target.Key.Episode:D2} parts must be unique and consecutive from 1.";
                return false;
            }
            if (parts.Count == 1 && parts[0] != 1)
            {
                mappings = result;
                error = $"A single source for S{target.Key.Season:D2}E{target.Key.Episode:D2} must be part 1.";
                return false;
            }
            var titles = target.Select(row => row.Title?.Trim() ?? string.Empty)
                .Where(title => title.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (titles.Count > 1)
            {
                mappings = result;
                error = $"S{target.Key.Season:D2}E{target.Key.Episode:D2} has conflicting custom titles.";
                return false;
            }
            var metadataContracts = target.Select(row => new
                {
                    Title = row.Title?.Trim() ?? string.Empty,
                    Summary = row.Summary?.Trim() ?? string.Empty,
                    Date = row.OriginallyAvailableAt?.Date,
                    Kind = row.ContentKind?.Trim().ToLowerInvariant(),
                    row.IncludeInMonitoring
                }).Distinct().Count();
            if (metadataContracts > 1)
            {
                mappings = result;
                error = $"S{target.Key.Season:D2}E{target.Key.Episode:D2} split parts have conflicting metadata or Wanted settings.";
                return false;
            }
        }

        if (sourceGroups.Count > 0 && result.Keys.Any(key => key.Item1 > 0 && !sourceGroups.Contains(key.Item1)))
        {
            var unknown = result.Keys.First(key => key.Item1 > 0 && !sourceGroups.Contains(key.Item1));
            mappings = result;
            error = $"Source mapping {SourceLabel(unknown.Item1, unknown.Item2)} has no matching source folder name.";
            return false;
        }

        mappings = result;
        error = result.Count == 0 ? "Add at least one custom episode mapping." : null;
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
        if (TryParseDetailed(profile!, out var map, out _) && map.TryGetValue((sourceSeason, sourceEpisode), out var found))
        {
            target = new EpisodeRef { Season = found.Episode.Season, Episode = found.Episode.Episode };
            return true;
        }
        target = new EpisodeRef();
        return false;
    }

    public static bool TryTranslateDetailed(SeriesEpisodeOrderProfileDto? profile, int sourceSeason,
        int sourceEpisode, out EpisodeMapTarget target)
    {
        if (!IsActive(profile))
        {
            target = new EpisodeMapTarget(new EpisodeRef { Season = sourceSeason, Episode = sourceEpisode });
            return true;
        }
        if (TryParseDetailed(profile!, out var map, out _) && map.TryGetValue((sourceSeason, sourceEpisode), out var found))
        {
            target = new EpisodeMapTarget(new EpisodeRef
            {
                Season = found.Episode.Season,
                Episode = found.Episode.Episode
            }, found.Part);
            return true;
        }
        target = new EpisodeMapTarget(new EpisodeRef());
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
        var translated = TryTranslateFileDetailed(profile, filePath, parsedSeason, sourceEpisode,
            out var detailed);
        target = detailed.Episode;
        return translated;
    }

    public static bool TryTranslateFileDetailed(SeriesEpisodeOrderProfileDto? profile, string filePath,
        int parsedSeason, int sourceEpisode, out EpisodeMapTarget target)
    {
        if (!IsActive(profile))
            return TryTranslateDetailed(profile, parsedSeason, sourceEpisode, out target);
        if (!TryParseDetailed(profile!, out var map, out _))
        {
            target = new EpisodeMapTarget(new EpisodeRef());
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
                    target = new EpisodeMapTarget(new EpisodeRef());
                    return false;
                }
                // A proven collection folder outranks an arc's own S1/S2 token. Mixing both identities can
                // accidentally map an embedded sub-series through the first franchise group.
                sourceKeys = [(parentSeason, sourceEpisode)];
            }
            else if (parsedSeason == 0)
                sourceKeys.Insert(0, (parentSeason, sourceEpisode));
        }
        else if (TryResolveNamedSourceGroup(profile, filePath, out var namedSourceSeason))
        {
            // Standalone arc releases usually reset their own numbering to S01/E01. The configured,
            // uniquely-matched arc name is stronger evidence than that uploader-local season number.
            sourceKeys = [(namedSourceSeason, sourceEpisode)];
        }

        var matches = sourceKeys.Distinct()
            .Where(map.ContainsKey)
            .Select(key => map[key])
            .DistinctBy(destination => (destination.Episode.Season, destination.Episode.Episode, destination.Part))
            .ToList();
        if (matches.Count == 1)
        {
            target = new EpisodeMapTarget(new EpisodeRef
            {
                Season = matches[0].Episode.Season,
                Episode = matches[0].Episode.Episode
            }, matches[0].Part);
            return true;
        }

        target = new EpisodeMapTarget(new EpisodeRef());
        return false;
    }

    /// <summary>
    /// Resolve a standalone release or path to one explicitly configured source group by title. A match
    /// is accepted only when the complete group name occurs as a contiguous token sequence and exactly
    /// one group matches; generic or overlapping names therefore cannot silently remap an episode.
    /// </summary>
    public static bool TryResolveNamedSourceGroup(SeriesEpisodeOrderProfileDto? profile, string value,
        out int sourceSeason)
    {
        sourceSeason = 0;
        if (!IsActive(profile) || string.IsNullOrWhiteSpace(value)) return false;

        var valueTokens = TitleTokens(value);
        if (valueTokens.Count == 0) return false;
        var matches = (profile!.SourceGroups ?? [])
            .Where(group => group.SourceSeason > 0)
            .Where(group =>
            {
                var expected = TitleTokens(group.Name);
                return expected.Count > 0
                       && string.Concat(expected).Length >= 5
                       && ContainsTokenSequence(valueTokens, expected);
            })
            .Select(group => group.SourceSeason)
            .Distinct()
            .Take(2)
            .ToList();
        if (matches.Count != 1) return false;
        sourceSeason = matches[0];
        return true;
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
        if (TryParseDetailed(profile!, out var map, out _))
        {
            foreach (var pair in map)
            {
                if (pair.Value.Episode.Season != canonicalSeason || pair.Value.Episode.Episode != canonicalEpisode) continue;
                source = new EpisodeRef { Season = pair.Key.Season, Episode = pair.Key.Episode };
                return true;
            }
        }
        source = new EpisodeRef();
        return false;
    }

    public static IReadOnlyList<EpisodeRef> SourcesForCanonicalEpisode(
        SeriesEpisodeOrderProfileDto profile, int canonicalSeason, int canonicalEpisode)
    {
        if (!TryParseDetailed(profile, out var map, out _)) return [];
        return map.Where(pair => pair.Value.Episode.Season == canonicalSeason
                                 && pair.Value.Episode.Episode == canonicalEpisode)
            .OrderBy(pair => pair.Value.Part)
            .Select(pair => new EpisodeRef { Season = pair.Key.Season, Episode = pair.Key.Episode })
            .ToList();
    }

    /// <summary>Returns release-numbered episodes whose canonical targets belong to the requested seasons.</summary>
    public static IReadOnlyList<EpisodeRef> SourcesForCanonicalSeasons(
        SeriesEpisodeOrderProfileDto profile, IEnumerable<int> canonicalSeasons)
    {
        if (!TryParseDetailed(profile, out var map, out _)) return [];
        var wanted = canonicalSeasons.ToHashSet();
        return map.Where(x => wanted.Contains(x.Value.Episode.Season))
            .Select(x => new EpisodeRef { Season = x.Key.Season, Episode = x.Key.Episode })
            .OrderBy(x => x.Season).ThenBy(x => x.Episode).ToList();
    }

    public static IReadOnlyList<EpisodeRef> SourceSeasonCoverage(SeriesEpisodeOrderProfileDto profile, int? sourceSeason)
    {
        if (!TryParseDetailed(profile, out var map, out _)) return [];
        return map.Where(x => sourceSeason is null || x.Key.Season == sourceSeason)
            .Select(x => new EpisodeRef { Season = x.Value.Episode.Season, Episode = x.Value.Episode.Episode })
            .OrderBy(x => x.Season).ThenBy(x => x.Episode).ToList();
    }

    public static string Normalize(SeriesEpisodeOrderProfileDto profile)
    {
        if (profile.CustomMetadataEnabled)
        {
            if (!TryParseDetailed(profile, out var custom, out _)) return profile.MappingsText.Trim();
            return string.Join('\n', custom.OrderBy(x => x.Key.Season).ThenBy(x => x.Key.Episode)
                .Select(x => $"{SourceLabel(x.Key.Season, x.Key.Episode)} -> "
                             + $"S{x.Value.Episode.Season:D2}E{x.Value.Episode.Episode:D2}"
                             + (x.Value.Part > 1 || custom.Values.Count(value =>
                                 value.Episode.Season == x.Value.Episode.Season
                                 && value.Episode.Episode == x.Value.Episode.Episode) > 1
                                 ? $" pt{x.Value.Part}" : string.Empty)));
        }
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

    private static bool ContainsTokenSequence(IReadOnlyList<string> value, IReadOnlyList<string> expected)
    {
        if (expected.Count > value.Count) return false;
        for (var start = 0; start <= value.Count - expected.Count; start++)
            if (value.Skip(start).Take(expected.Count).SequenceEqual(expected))
                return true;
        return false;
    }

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
