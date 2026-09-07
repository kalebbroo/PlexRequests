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
        var selectedCoverage = new Dictionary<(int Season, int Episode), int>();
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
            if (namedSequence is not null
                && namedSequence.CanonicalEpisodes.TryGetValue(file.Path, out var sequenceEpisode)
                && item.Season is int sequenceSeason)
                coverage.Add((sequenceSeason, sequenceEpisode));
            else
            {
                if (parsed.FractionalEpisodeNumber)
                {
                    unmappedVideos.Add($"{file.Path} [fractional/special episode]");
                    continue;
                }
                var episodes = parsed.EpisodeNumbers.Distinct().OrderBy(number => number).ToList();
                if (parsed.Season is not int sourceSeason || episodes.Count == 0 || !IsContiguous(episodes))
                    continue; // extras and unrelated videos stay at priority zero

                if (!EpisodeOrderMapping.IsActive(episodeOrderProfile)
                    && item.Season is null
                    && job.CanonicalSeasons.Count > 0)
                {
                    if (AnimeNamedCollectionMapper.TryMapFile(file.Path, job, parsed, out var namedCoverage))
                        coverage.AddRange(namedCoverage.Select(target => (target.Season, target.Episode)));
                    else
                        unmappedVideos.Add(file.Path);
                }
                else
                {
                    foreach (var sourceEpisode in episodes)
                    {
                        EpisodeRef target;
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
                            target = new EpisodeRef { Season = canonicalSeason, Episode = sourceEpisode };
                        }
                        else if (!EpisodeOrderMapping.TryTranslateFile(episodeOrderProfile, file.Path,
                                     sourceSeason, sourceEpisode, out target))
                        {
                            unmappedVideos.Add(file.Path);
                            coverage.Clear();
                            break;
                        }
                        coverage.Add((target.Season, target.Episode));
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

            foreach (var target in coverage)
            {
                if (selectedCoverage.TryGetValue(target, out var otherIndex))
                    return ManifestPreflightDecision.Reject(
                        $"Canonical S{target.Season:D2}E{target.Episode:D2} appears in both {manifest.Files[otherIndex].Path} and {file.Path}.",
                        manifest.Files.Count);
                selectedCoverage[target] = index;
            }
            selected[index] = true;
            try { selectedBytes = checked(selectedBytes + file.SizeBytes); }
            catch (OverflowException)
            {
                return ManifestPreflightDecision.Reject("Selected torrent byte total overflowed the safe range.", manifest.Files.Count);
            }
        }

        var missing = targets.Where(target => !selectedCoverage.ContainsKey(target))
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
