using System.Text.RegularExpressions;
using PlexRequests.Downloader.Download;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Worker;

internal sealed record ManifestPreflightDecision(
    bool Accepted,
    string Detail,
    IReadOnlyList<bool> WantedFiles,
    IReadOnlyList<EpisodeRef> CanonicalCoverage,
    int? FractionalEpisodeInsertionAfter = null)
{
    public static ManifestPreflightDecision Reject(string detail, int fileCount) =>
        new(false, detail, Enumerable.Repeat(false, fileCount).ToList(), []);
}

/// <summary>Turns an untrusted anime torrent manifest into a fail-closed file selection. No fuzzy title
/// decision occurs here: every selected video must resolve through the job's immutable episode-order policy,
/// cover the exact outstanding canonical contract once, and remain under the configured selected-byte cap.</summary>
internal static class AnimeManifestPreflight
{
    public static ManifestPreflightDecision Evaluate(
        AcquisitionManifest manifest,
        FulfillmentJobDto job,
        DownloadPlanItem item,
        IReleaseParser parser,
        IReadOnlyCollection<string> videoExtensions,
        double maxSelectedGb,
        IReadOnlyCollection<string>? subtitleExtensions = null) =>
        EvaluateWithEpisodeOrder(manifest, job, item, parser, videoExtensions, maxSelectedGb,
            job.EpisodeOrderProfile, subtitleExtensions);

    /// <summary>Evaluate the same immutable target contract against one proposed episode order without
    /// mutating the claimed job. Used only during payload-free order discovery.</summary>
    public static ManifestPreflightDecision EvaluateWithEpisodeOrder(
        AcquisitionManifest manifest,
        FulfillmentJobDto job,
        DownloadPlanItem item,
        IReleaseParser parser,
        IReadOnlyCollection<string> videoExtensions,
        double maxSelectedGb,
        SeriesEpisodeOrderProfileDto? episodeOrderProfile,
        IReadOnlyCollection<string>? subtitleExtensions = null)
    {
        if (!job.IsAnime || !item.IsPack)
            return ManifestPreflightDecision.Reject("Manifest preflight is restricted to anime collection packs.", manifest.Files.Count);
        var targets = (item.NeededEpisodeRefs ?? [])
            .Where(target => target.Season >= 0 && target.Episode > 0)
            .Select(target => (target.Season, target.Episode))
            .ToHashSet();
        if (targets.Count == 0)
            return ManifestPreflightDecision.Reject("The anime collection has no exact canonical episode target set.", manifest.Files.Count);
        if (manifest.Files.Count == 0)
            return ManifestPreflightDecision.Reject("Torrent metadata contains no files.", 0);

        var extensions = videoExtensions
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.StartsWith('.') ? extension : $".{extension}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var subtitleSet = (subtitleExtensions ?? [])
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.StartsWith('.') ? extension : $".{extension}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = Enumerable.Repeat(false, manifest.Files.Count).ToArray();
        var selectedCoverage = new Dictionary<(int Season, int Episode), Dictionary<int, int>>();
        var expectedParts = new Dictionary<(int Season, int Episode), HashSet<int>>();
        if (EpisodeOrderMapping.IsActive(episodeOrderProfile)
            && EpisodeOrderMapping.TryParseDetailed(episodeOrderProfile!, out var detailedMap, out _))
        {
            expectedParts = detailedMap.Values.GroupBy(value =>
                    (value.Episode.Season, value.Episode.Episode))
                .ToDictionary(group => group.Key, group => group.Select(value => value.Part).ToHashSet());
        }
        var unmappedVideos = new List<string>();
        long selectedBytes = 0;

        NamedSeasonSequenceMap? namedSequence = null;
        if (!EpisodeOrderMapping.IsActive(episodeOrderProfile)
            && item.SourceSeason is int sequenceSourceSeason
            && item.Season is int sequenceCanonicalSeason
            && CanonicalEpisodeCount(job, sequenceCanonicalSeason) is int sequenceEpisodeCount)
        {
            var videoPaths = manifest.Files
                .Where(file => extensions.Contains(Path.GetExtension(file.Path)))
                .Select(file => file.Path).ToList();
            if (AnimeNamedSeasonSequenceMapper.TryDetect(videoPaths, job,
                    sequenceSourceSeason, sequenceCanonicalSeason, sequenceEpisodeCount, parser,
                    out namedSequence, out var sequenceFailure)
                && namedSequence is null)
                return ManifestPreflightDecision.Reject(
                    sequenceFailure ?? "Fractional named season sequence could not be mapped safely.",
                    manifest.Files.Count);
        }

        for (var index = 0; index < manifest.Files.Count; index++)
        {
            var file = manifest.Files[index];
            if (!SafeRelativePath(file.Path))
                return ManifestPreflightDecision.Reject($"Torrent metadata contains an unsafe path: {file.Path}", manifest.Files.Count);
            var extension = Path.GetExtension(file.Path);
            if (subtitleSet.Contains(extension))
            {
                // Subtitle sidecars are tiny compared with video and may be the only valid Smart-anime
                // fallback. Keep them available for the organizer's conservative filename pairing and
                // MediaInfo policy check; unrelated sidecars are discarded with staging after import.
                selected[index] = true;
                try { selectedBytes = checked(selectedBytes + Math.Max(0, file.SizeBytes)); }
                catch (OverflowException)
                {
                    return ManifestPreflightDecision.Reject("Selected torrent byte total overflowed the safe range.", manifest.Files.Count);
                }
                continue;
            }
            if (!extensions.Contains(extension)) continue;

            var parsed = parser.Parse(Path.GetFileName(file.Path));
            var coverage = new List<(int Season, int Episode)>();
            var mappedParts = new List<((int Season, int Episode) Target, int Part)>();
            if (namedSequence is not null
                && namedSequence.CanonicalEpisodes.TryGetValue(file.Path, out var sequenceEpisode)
                && item.Season is int sequenceSeason)
            {
                coverage.Add((sequenceSeason, sequenceEpisode));
                mappedParts.Add(((sequenceSeason, sequenceEpisode), 1));
            }
            else
            {
                if (parsed.FractionalEpisodeNumber)
                {
                    unmappedVideos.Add($"{file.Path} [fractional/special episode]");
                    continue;
                }
                var episodes = parsed.EpisodeNumbers.Distinct().OrderBy(number => number).ToList();
                if (episodes.Count == 0 && parsed.Season is null && !EpisodeOrderMapping.IsActive(episodeOrderProfile))
                    episodes = FallbackAbsoluteEpisodes(item, file.Path).ToList();
                // The parser reads a bare absolute number ("DNAngel - 25 - ...") as season 0. Only that case, with no
                // season marker in the file name, can be mapped as a provisional season.
                var absoluteSeason = parsed.Season is null or 0
                    ? ProvisionalAbsoluteSeason(job, item, episodeOrderProfile, file.Path, episodes)
                    : null;
                if ((parsed.Season is null && absoluteSeason is null) || episodes.Count == 0 || !IsContiguous(episodes))
                    continue; // extras and unrelated videos stay at priority zero
                int sourceSeason = absoluteSeason ?? parsed.Season ?? 0;

                if (!EpisodeOrderMapping.IsActive(episodeOrderProfile)
                    && item.Season is null
                    && job.CanonicalSeasons.Count > 0)
                {
                    if (AnimeNamedCollectionMapper.TryMapFile(file.Path, job, parsed, out var namedCoverage))
                    {
                        coverage.AddRange(namedCoverage.Select(target => (target.Season, target.Episode)));
                        mappedParts.AddRange(namedCoverage.Select(target => ((target.Season, target.Episode), 1)));
                    }
                    else
                        unmappedVideos.Add(file.Path);
                }
                else
                {
                    foreach (var sourceEpisode in episodes)
                    {
                        EpisodeMapTarget destination;
                        if (!EpisodeOrderMapping.IsActive(episodeOrderProfile)
                            && item.SourceSeason is int expectedSource
                            && item.Season is int canonicalSeason
                            && expectedSource != canonicalSeason)
                        {
                            var namedAbsoluteEpisode = sourceSeason == 0
                                && MatchesNamedCanonicalSeason(job, file.Path, expectedSource, canonicalSeason);
                            if (sourceSeason != expectedSource && !namedAbsoluteEpisode)
                            {
                                unmappedVideos.Add(file.Path);
                                coverage.Clear();
                                break;
                            }
                            if (namedAbsoluteEpisode
                                && (CanonicalEpisodeCount(job, canonicalSeason) is not int expectedCount
                                    || sourceEpisode > expectedCount))
                                return ManifestPreflightDecision.Reject(
                                    $"{file.Path} declares absolute episode {sourceEpisode}, outside canonical " +
                                    $"S{canonicalSeason:D2}'s known episode range.", manifest.Files.Count);
                            destination = new EpisodeMapTarget(new EpisodeRef
                                { Season = canonicalSeason, Episode = sourceEpisode });
                        }
                        else if (!EpisodeOrderMapping.TryTranslateFileDetailed(episodeOrderProfile, file.Path,
                                     sourceSeason, sourceEpisode, out destination))
                        {
                            unmappedVideos.Add(file.Path);
                            coverage.Clear();
                            break;
                        }
                        var target = (destination.Episode.Season, destination.Episode.Episode);
                        coverage.Add(target);
                        mappedParts.Add((target, destination.Part));
                    }
                }
            }
            coverage = coverage.Distinct().OrderBy(target => target.Season).ThenBy(target => target.Episode).ToList();
            if (coverage.Count == 0 || coverage.All(target => !targets.Contains(target))) continue;
            if (coverage.Any(target => !targets.Contains(target)))
                return ManifestPreflightDecision.Reject(
                    $"{file.Path} combines requested episodes with canonical episodes outside this job's target set.",
                    manifest.Files.Count);
            if (file.SizeBytes <= 0)
                return ManifestPreflightDecision.Reject($"Selected video has no trustworthy byte length: {file.Path}", manifest.Files.Count);

            if (mappedParts.GroupBy(item => item.Target).Any(group => group.Count() > 1))
                return ManifestPreflightDecision.Reject(
                    $"{file.Path} maps more than one source identity to the same canonical episode; split parts must be separate files.",
                    manifest.Files.Count);
            foreach (var (target, part) in mappedParts)
            {
                if (!selectedCoverage.TryGetValue(target, out var parts))
                {
                    parts = new Dictionary<int, int>();
                    selectedCoverage[target] = parts;
                }
                if (parts.TryGetValue(part, out var otherIndex))
                    return ManifestPreflightDecision.Reject(
                        $"Canonical S{target.Season:D2}E{target.Episode:D2} part {part} appears in both {manifest.Files[otherIndex].Path} and {file.Path}.",
                        manifest.Files.Count);
                parts[part] = index;
            }
            selected[index] = true;
            try { selectedBytes = checked(selectedBytes + file.SizeBytes); }
            catch (OverflowException)
            {
                return ManifestPreflightDecision.Reject("Selected torrent byte total overflowed the safe range.", manifest.Files.Count);
            }
        }

        var missing = targets.Where(target => !selectedCoverage.TryGetValue(target, out var parts)
                                              || expectedParts.TryGetValue(target, out var expected)
                                              && !parts.Keys.ToHashSet().SetEquals(expected))
            .OrderBy(target => target.Season).ThenBy(target => target.Episode).ToList();
        if (missing.Count > 0)
        {
            var sample = string.Join(",", missing.Take(12).Select(target => $"S{target.Season:D2}E{target.Episode:D2}"));
            var suffix = missing.Count > 12 ? $" (+{missing.Count - 12} more)" : string.Empty;
            var unmapped = unmappedVideos.Count == 0
                ? string.Empty
                : $" {unmappedVideos.Count} numbered video file(s) did not match this order"
                  + $" (for example: {string.Join("; ", unmappedVideos.Distinct().Take(3))}).";
            return ManifestPreflightDecision.Reject(
                $"Torrent manifest is missing {missing.Count} requested canonical episode(s): {sample}{suffix}.{unmapped}",
                manifest.Files.Count);
        }

        var selectedGb = selectedBytes / 1024d / 1024d / 1024d;
        if (selectedGb > maxSelectedGb)
            return ManifestPreflightDecision.Reject(
                $"Selected anime payload is {selectedGb:F1} GB, above the {maxSelectedGb:F1} GB pack limit.",
                manifest.Files.Count);

        var canonical = selectedCoverage.Keys.OrderBy(target => target.Season).ThenBy(target => target.Episode)
            .Select(target => new EpisodeRef { Season = target.Season, Episode = target.Episode }).ToList();
        return new ManifestPreflightDecision(true,
            $"Manifest proved {canonical.Count} canonical episode(s); selected {selected.Count(value => value)}/{selected.Length} files ({selectedGb:F1} GB).",
            selected, canonical, namedSequence?.InsertionAfter);
    }

    /// <summary>
    /// The season of a file that carries only an absolute episode number ("DNAngel - 25 - The Black Wings"). Only a
    /// provisional single-season collection qualifies: the request names one season, the file holds one episode, that
    /// episode is still needed, and the file name carries the requested show's title. Anything else stays unmapped.
    /// </summary>
    internal static int? ProvisionalAbsoluteSeason(FulfillmentJobDto job, DownloadPlanItem item,
        SeriesEpisodeOrderProfileDto? episodeOrderProfile, string filePath, IReadOnlyList<int> episodes)
    {
        if (item.Season is not int season || item.SourceSeason is not null) return null;
        if (EpisodeOrderMapping.IsActive(episodeOrderProfile) || episodes.Count != 1) return null;
        if (Regex.IsMatch(Path.GetFileName(filePath), @"(?i)\bS\d{1,2}(E\d|\b)|\bseason\s*\d")) return null;
        if (item.NeededEpisodeRefs is not { } needed
            || !needed.Any(target => target.Season == season && target.Episode == episodes[0])) return null;
        var title = Letters(job.Title);
        return title.Length >= 2 && Letters(Path.GetFileName(filePath)).Contains(title, StringComparison.Ordinal)
            ? season
            : null;
    }

    /// <summary>
    /// A bare episode number the parser could not read ("D.N.Angel-25-TheBlackWings(anime-mx)"). Only a single
    /// needed episode of the provisional season, named once in the file name outside its bracketed tags, qualifies.
    /// </summary>
    internal static IReadOnlyList<int> FallbackAbsoluteEpisodes(DownloadPlanItem item, string filePath)
    {
        if (item.Season is not int season || item.NeededEpisodeRefs is not { } needed) return [];
        var wanted = needed.Where(target => target.Season == season).Select(target => target.Episode).ToHashSet();
        var name = Regex.Replace(Path.GetFileNameWithoutExtension(filePath), @"\[[^\]]*\]|\([^)]*\)", " ");
        var numbers = Regex.Matches(name, @"(?<!\d)\d{1,3}(?!\d)").Select(match => int.Parse(match.Value))
            .Where(wanted.Contains).Distinct().ToList();
        return numbers.Count == 1 ? numbers : [];
    }

    /// <summary>
    /// Whether one file, numbered only by its absolute episode, belongs to a provisional season. The request must need
    /// that episode, the file name must carry the requested show's title, and it must name no season.
    /// </summary>
    internal static bool IsProvisionalAbsoluteFile(FulfillmentJobDto job, string filePath, IReadOnlyList<int> episodes,
        int season, IEnumerable<(int Season, int Episode)> needed)
    {
        if (!job.IsAnime || episodes.Count != 1) return false;
        if (!needed.Any(target => target.Season == season && target.Episode == episodes[0])) return false;
        var name = Path.GetFileName(filePath);
        if (Regex.IsMatch(name, @"(?i)\bS\d{1,2}(E\d|\b)|\bseason\s*\d")) return false;
        var title = Letters(job.Title);
        return title.Length >= 2 && Letters(name).Contains(title, StringComparison.Ordinal);
    }

    private static string Letters(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    internal static bool MatchesNamedCanonicalSeason(FulfillmentJobDto job, string releasePath,
        int sourceSeason, int canonicalSeason) => job.CanonicalSeasons.Count > 0
        ? AnimeSeasonIdentity.MatchesCanonicalSeason(releasePath, job.Title, job.CanonicalSeasons,
            sourceSeason, canonicalSeason)
        : AnimeSeasonIdentity.MatchesCanonicalSeason(releasePath, job.Title, job.SeasonTargets,
            sourceSeason, canonicalSeason);

    internal static int? CanonicalEpisodeCount(FulfillmentJobDto job, int canonicalSeason)
    {
        var count = job.CanonicalSeasons.FirstOrDefault(season => season.Season == canonicalSeason)?.EpisodeCount
                    ?? job.SeasonTargets.FirstOrDefault(season => season.Season == canonicalSeason)?.EpisodeCount;
        return count > 0 ? count : null;
    }

    private static bool SafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || Path.IsPathRooted(path)) return false;
        var components = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return components.Length > 0 && components.All(component => component is not "." and not "..");
    }

    private static bool IsContiguous(IReadOnlyList<int> episodes)
    {
        if (episodes.Count == 0) return false;
        for (var index = 1; index < episodes.Count; index++)
            if (episodes[index] != episodes[index - 1] + 1) return false;
        return true;
    }
}
