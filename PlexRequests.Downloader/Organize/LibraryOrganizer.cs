using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Ranking;
using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using PlexRequestsHosted.Shared;
using System.Text.RegularExpressions;

namespace PlexRequests.Downloader.Organize;

public interface ILibraryOrganizer
{
    Task<ImportResult> OrganizeAsync(FulfillmentJobDto job, TransferItem transfer, string sourcePath, EffectiveLibraryOrganization prefs, CancellationToken ct);
}

/// <summary>
/// Turns a finished torrent's raw payload into properly-named, correctly-placed files in the Plex
/// library: extracts archives, splits season packs into per-episode files, pairs subtitles, and renames
/// everything per the admin-configured templates. Replaces the old LibraryImporter's "dump the raw
/// torrent name into the library root" behavior.
/// </summary>
public class LibraryOrganizer(
    IArchiveExtractor extractor,
    ISeasonPackSplitter splitter,
    IEpisodeTitleProvider episodeTitles,
    IPlexNamingService naming,
    IReleaseParser parser,
    IMediaTrackInspector trackInspector,
    IMultipartEpisodeJoiner multipartJoiner,
    ILogger<LibraryOrganizer> logger) : ILibraryOrganizer
{
    public async Task<ImportResult> OrganizeAsync(FulfillmentJobDto job, TransferItem transfer, string sourcePath, EffectiveLibraryOrganization prefs, CancellationToken ct)
    {
        string? stagingRoot = null;
        try
        {
            List<string> files;
            if (Directory.Exists(sourcePath))
                files = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories).ToList();
            else if (File.Exists(sourcePath))
                files = new List<string> { sourcePath };
            else
                return ImportResult.Fail($"Source path does not exist: {sourcePath}");

            // Archive extraction: operate on the extracted files afterward, never the raw archive parts —
            // this is what makes scene-style RAR-packed releases visible at all (previously invisible to a
            // plain extension filter, and silently treated as a zero-file "success").
            var archiveEntryPoints = files.Where(f => extractor.LooksLikeArchive(f) && !extractor.IsContinuationVolume(f)).ToList();
            if (archiveEntryPoints.Count > 0 && prefs.ExtractArchives)
            {
                // Staged under the source's own parent directory (same filesystem as the download), so a
                // Hardlink transfer still works for the extracted files afterward.
                var parent = Directory.Exists(sourcePath) ? Directory.GetParent(sourcePath)?.FullName : Path.GetDirectoryName(sourcePath);
                stagingRoot = Path.Combine(parent ?? Path.GetTempPath(), ".plexrequests-staging", $"{job.Id}-{transfer.TransferId}");
                foreach (var archive in archiveEntryPoints)
                    await extractor.ExtractAsync(archive, stagingRoot, ct);
                files = Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories).ToList();
            }

            var mediaFiles = job.MediaType == MediaType.Music
                ? files.Where(IsAudio(prefs))
                    // Direct sources already validate every expected track before reporting completion.
                    // The admin's general torrent-size floor must not discard a legitimate short intro.
                    .Where(f => transfer.Protocol == AcquisitionProtocol.DirectAudio || PassesMinAudioSize(f, prefs))
                    .ToList()
                : files.Where(IsVideo(prefs)).Where(f => PassesMinVideoSize(f, prefs)).ToList();

            var contentRoot = stagingRoot ?? sourcePath;
            var records = job.MediaType switch
            {
                MediaType.Movie => await OrganizeMovieAsync(job, mediaFiles, files, prefs, ct),
                MediaType.Music => OrganizeMusic(job, contentRoot, mediaFiles, files, prefs),
                _ => await OrganizeTvAsync(job, transfer, mediaFiles, files, prefs,
                    await ExpectedEpisodeCountAsync(job, transfer, ct), ct)
            };

            var primaryType = job.MediaType == MediaType.Music ? "audio" : "video";
            if (records.Count(r => r.FileType == primaryType) == 0)
            {
                var reason = archiveEntryPoints.Count > 0 && !prefs.ExtractArchives
                    ? $"No {primaryType} files found for \"{job.Title}\" — source appears to be an archive but archive extraction is disabled"
                    : $"No {primaryType} files found for \"{job.Title}\" after import (checked {files.Count} file(s))";
                return ImportResult.Fail(reason);
            }

            logger.LogInformation("Organized \"{Title}\": {Count} file(s) placed", job.Title, records.Count);
            return ImportResult.Ok(records);
        }
        catch (MediaPolicyViolationException ex)
        {
            logger.LogWarning("Media policy rejected \"{Title}\": {Reason}", job.Title, ex.Message);
            return ImportResult.Fail(ex.Message, BlocklistReason.MediaPolicyMismatch);
        }
        catch (EpisodeMappingException ex)
        {
            logger.LogWarning("Episode mapping rejected \"{Title}\": {Reason}", job.Title, ex.Message);
            return ImportResult.Fail(ex.Message, BlocklistReason.EpisodeMappingAmbiguous);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Organize failed for \"{Title}\" from {Source}", job.Title, sourcePath);
            return ImportResult.Fail($"Import failed: {ex.Message}");
        }
        finally
        {
            if (stagingRoot is not null)
            {
                try { Directory.Delete(stagingRoot, recursive: true); }
                catch (Exception ex) { logger.LogDebug(ex, "Staging cleanup skipped for {Path}", stagingRoot); }
            }
        }
    }

    private async Task<int?> ExpectedEpisodeCountAsync(FulfillmentJobDto job, TransferItem transfer, CancellationToken ct)
    {
        if (transfer.Season is not int season) return null;
        var fromTargets = job.SeasonTargets.FirstOrDefault(t => t.Season == season)?.EpisodeCount;
        if (fromTargets is > 0) return fromTargets;
        var episodes = await episodeTitles.GetSeasonEpisodesAsync(job.TmdbId, season, ct);
        return episodes.Count > 0 ? episodes.Count : null;
    }

    private async Task<List<ImportedFileRecord>> OrganizeMovieAsync(FulfillmentJobDto job,
        List<string> videoFiles, List<string> allFiles, EffectiveLibraryOrganization prefs, CancellationToken ct)
    {
        var records = new List<ImportedFileRecord>();
        var best = videoFiles.OrderByDescending(f => SafeLength(f)).FirstOrDefault();
        if (best is null) return records;

        var inspected = await InspectSelectionAsync(job, [best], allFiles, prefs, ct);
        var dest = naming.BuildMoviePath(prefs, job, Path.GetExtension(best));
        TransferOne(best, dest, null, null, "video", records, prefs, inspected.GetValueOrDefault(best),
            playbackJob: job);
        PairSubtitle(job, best, dest, allFiles, prefs, null, null, records);
        return records;
    }

    private List<ImportedFileRecord> OrganizeMusic(FulfillmentJobDto job, string sourcePath,
        List<string> audioFiles, List<string> allFiles, EffectiveLibraryOrganization prefs)
    {
        var records = new List<ImportedFileRecord>();
        if (string.IsNullOrWhiteSpace(prefs.MusicPath))
            throw new InvalidOperationException("Music library path is not configured");
        if (job.Music is null)
            throw new InvalidOperationException("Music metadata context is missing; the job will retry after metadata refresh");

        var music = job.Music;
        var artist = CleanLabel(music.Artist, "Unknown Artist");

        if (music.Kind == MediaKind.Artist)
        {
            // A discography already contains album folders. Retain that hierarchy beneath a single,
            // sanitized artist root instead of flattening hundreds of tracks into one directory.
            var root = Directory.Exists(sourcePath) ? sourcePath : Path.GetDirectoryName(sourcePath)!;
            var artistRoot = Path.Combine(prefs.MusicPath, NamingTemplateEngine.SanitizeComponent(artist));
            foreach (var file in audioFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(root, file);
                if (relative.StartsWith("..", StringComparison.Ordinal)) relative = Path.GetFileName(file);
                var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries).Select(NamingTemplateEngine.SanitizeComponent).ToArray();
                var safeRelative = parts.Length > 1
                    ? Path.Combine(parts)
                    : Path.Combine("Unknown Album", parts.FirstOrDefault() ?? Path.GetFileName(file));
                TransferOne(file, Path.Combine(artistRoot, safeRelative), null, null, "audio", records, prefs);
            }
            return records;
        }

        if (music.Kind == MediaKind.Track)
        {
            var wanted = CleanLabel(music.Track ?? job.Title, job.Title);
            var namedMatches = audioFiles
                .Where(f => ContainsNormalized(Path.GetFileNameWithoutExtension(f), wanted))
                .OrderByDescending(SafeLength).ToList();
            var best = namedMatches.FirstOrDefault();
            if (best is null && audioFiles.Count == 1) best = audioFiles[0];
            if (best is null && audioFiles.Count > 1)
                throw new InvalidOperationException(
                    $"Track payload is ambiguous: none of its {audioFiles.Count} audio files match '{wanted}'");
            if (best is null) return records;
            var album = CleanLabel(music.Album, "Singles");
            var dest = naming.BuildMusicTrackPath(prefs, job, artist, album, 1, 1, wanted, Path.GetExtension(best));
            TransferOne(best, dest, null, null, "audio", records, prefs);
            return records;
        }

        var albumTitle = CleanLabel(music.Album ?? job.Title, job.Title);
        var expected = (music.Tracks ?? []).OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList();
        var expectedCount = Math.Max(music.TrackCount, expected.Count);
        if (expected.Count == 0)
            throw new InvalidOperationException("Album metadata has no track completion contract; metadata will be refreshed before retry");
        if (expectedCount > 0 && audioFiles.Count < expectedCount)
            throw new InvalidOperationException($"Album payload is incomplete: metadata expects {expectedCount} tracks but the release contains {audioFiles.Count} audio files");

        // Resolve the entire expected tracklist before writing anything. The old final fallback assigned
        // an arbitrary remaining title to an unidentifiable file, which could rename wrong audio into the
        // expected contract and then fool Plex verification. Title matches are strongest; disc/track
        // numbers are the safe fallback. Unmatched files are allowed only as extras after every expected
        // track has an unambiguous source.
        var orderedFiles = audioFiles.OrderBy(NaturalTrackKey, StringComparer.OrdinalIgnoreCase).ToList();
        var unused = new HashSet<MusicTrackMetadataDto>(expected);
        var assignments = new Dictionary<string, MusicTrackMetadataDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in orderedFiles)
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var matches = unused.Where(t => ContainsNormalized(stem, t.Title)).ToList();
            if (matches.Count != 1) continue;
            assignments[file] = matches[0];
            unused.Remove(matches[0]);
        }
        foreach (var file in orderedFiles.Where(x => !assignments.ContainsKey(x)))
        {
            var parsed = ParseTrackNumbers(file);
            if (parsed.Track is null) continue;
            var matches = unused.Where(t => t.TrackNumber == parsed.Track
                && (parsed.Disc is null || Math.Max(1, t.DiscNumber) == parsed.Disc)).ToList();
            if (matches.Count != 1) continue;
            assignments[file] = matches[0];
            unused.Remove(matches[0]);
        }
        if (unused.Count > 0)
        {
            var sample = string.Join(", ", unused.OrderBy(x => x.DiscNumber).ThenBy(x => x.TrackNumber)
                .Take(3).Select(x => x.Title));
            throw new InvalidOperationException(
                $"Album payload is ambiguous: could not map {unused.Count} expected track(s){(sample.Length > 0 ? $" ({sample})" : string.Empty)}");
        }

        var nextExtra = expected.Select(t => t.TrackNumber).DefaultIfEmpty(0).Max() + 1;
        foreach (var file in orderedFiles)
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var parsed = ParseTrackNumbers(file);
            assignments.TryGetValue(file, out var track);

            var disc = Math.Max(1, track?.DiscNumber ?? parsed.Disc ?? 1);
            var number = Math.Max(1, track?.TrackNumber ?? nextExtra++);
            var title = CleanLabel(track?.Title, StripTrackPrefix(stem));
            var trackArtist = CleanLabel(track?.ArtistCredit, artist);
            var dest = naming.BuildMusicTrackPath(prefs, job, trackArtist, albumTitle, disc, number, title, Path.GetExtension(file));
            TransferOne(file, dest, null, null, "audio", records, prefs);
        }

        // Plex recognizes cover.jpg/folder.jpg beside the tracks. Keep one small artwork file without
        // copying arbitrary images bundled in a torrent (booklets/scans can be very large).
        var artwork = allFiles.FirstOrDefault(IsPreferredArtwork);
        if (artwork is not null && records.FirstOrDefault(r => r.FileType == "audio") is { } first)
        {
            var ext = Path.GetExtension(artwork).ToLowerInvariant();
            var dest = Path.Combine(Path.GetDirectoryName(first.DestinationPath)!, $"cover{ext}");
            try { TransferOne(artwork, dest, null, null, "artwork", records, prefs); }
            catch (Exception ex) { logger.LogWarning(ex, "Optional album artwork import skipped for {Title}", job.Title); }
        }
        return records;
    }

    private async Task<List<ImportedFileRecord>> OrganizeTvAsync(FulfillmentJobDto job, TransferItem transfer,
        List<string> videoFiles, List<string> allFiles, EffectiveLibraryOrganization prefs,
        int? expectedEpisodeCount, CancellationToken ct)
    {
        var records = new List<ImportedFileRecord>();

        if (!transfer.IsPack)
        {
            // Single-episode item — pick the largest video file (packs sometimes bundle a sample/extra
            // alongside the real episode even when it's not nominally a "pack").
            var best = videoFiles.OrderByDescending(f => SafeLength(f)).FirstOrDefault();
            if (best is null || transfer.Season is not int s || transfer.Episode is not int e) return records;

            if (EpisodeOrderMapping.IsActive(job.EpisodeOrderProfile))
            {
                if (job.EpisodeOrderProfile is { } order
                    && EpisodeOrderMapping.SourcesForCanonicalEpisode(order, s, e).Count > 1)
                    throw new EpisodeMappingException(
                        $"Canonical S{s:D2}E{e:D2} requires every configured split part; a standalone file cannot satisfy it.");
                var parsed = parser.Parse(Path.GetFileName(best));
                var sourceEpisodes = parsed.EpisodeNumbers.Distinct().ToList();
                if (parsed.Season is not int sourceSeason || sourceEpisodes.Count != 1
                    || !EpisodeOrderMapping.TryTranslate(job.EpisodeOrderProfile, sourceSeason,
                        sourceEpisodes[0], out var canonical)
                    || canonical.Season != s || canonical.Episode != e)
                    throw new EpisodeMappingException(
                        $"Standalone file '{Path.GetFileName(best)}' does not prove canonical identity S{s:D2}E{e:D2}; no files were imported.");
            }
            else if (transfer.SourceSeason is int sourceSeason && sourceSeason != s)
            {
                var parsed = parser.Parse(Path.GetFileName(best));
                var sourceEpisodes = parsed.EpisodeNumbers.Distinct().ToList();
                if (parsed.Season != sourceSeason || sourceEpisodes.Count != 1 || sourceEpisodes[0] != e)
                    throw new EpisodeMappingException(
                        $"Standalone file '{Path.GetFileName(best)}' does not prove named-season translation S{sourceSeason:D2}E{e:D2} -> S{s:D2}E{e:D2}; no files were imported.");
            }

            var inspected = await InspectSelectionAsync(job, [best], allFiles, prefs, ct);
            var title = await GetEpisodeTitleAsync(job, s, e, ct);
            var dest = naming.BuildEpisodePath(prefs, job, s, e, title, Path.GetExtension(best));
            var coverage = Coverage(s, [e]);
            TransferOne(best, dest, s, e, "video", records, prefs, inspected.GetValueOrDefault(best), coverage,
                job);
            PairSubtitle(job, best, dest, allFiles, prefs, s, e, records, coverage);
            return records;
        }

        if (transfer.Season is int season)
        {
            List<CanonicalFileMapping> mapped;
            if (transfer.FractionalEpisodeInsertionAfter is int insertionAfter)
                mapped = MapFractionalNamedSeasonFiles(job, transfer, videoFiles,
                    expectedEpisodeCount, insertionAfter);
            else if (EpisodeOrderMapping.IsActive(job.EpisodeOrderProfile))
                mapped = MapTranslatedFiles(job, videoFiles);
            else
            {
                var allowAbsoluteOrder = transfer.SourceSeason is int expectedSource
                    && expectedSource != season
                    && videoFiles.Where(file => parser.Parse(Path.GetFileName(file)).Season == 0)
                        .All(file => AnimeManifestPreflight.MatchesNamedCanonicalSeason(
                            job, file, expectedSource, season));
                var map = splitter.Map(videoFiles, season, expectedEpisodeCount, transfer.SourceSeason,
                    allowAbsoluteOrder);
                if (!map.IsUnambiguous)
                    throw new EpisodeMappingException(DescribeMappingFailure(season, map));
                mapped = map.Mappings.Select(x => new CanonicalFileMapping(x.FilePath,
                    x.Episodes.Select(e => new EpisodeRef { Season = x.Season, Episode = e }).ToList())).ToList();
            }

            // A translated source pack can span several Plex seasons. This transfer was selected for one
            // canonical season, so do not leak adjacent-season files into the library.
            mapped = mapped.Where(x => x.Coverage.All(e => e.Season == season)).ToList();
            // Keep only the canonical targets this transfer was selected to satisfy, then prove their union
            // before the first library write. The new target set also handles cross-season packs; this branch
            // retains compatibility with in-flight single-season state written by older workers.
            var canonicalTargets = CanonicalTargets(transfer);
            if (canonicalTargets.Count > 0)
            {
                var before = mapped.Count;
                mapped = RestrictToCanonicalTargets(mapped, canonicalTargets);
                var covered = mapped.SelectMany(m => m.Coverage)
                    .Select(x => (x.Season, x.Episode)).ToHashSet();
                var missing = canonicalTargets.Where(x => !covered.Contains(x))
                    .OrderBy(x => x.Season).ThenBy(x => x.Episode).ToList();
                if (missing.Count > 0)
                    throw new EpisodeMappingException(
                        $"Season pack cannot prove coverage for requested episode(s) {DescribeTargets(missing)}; no files were imported.");
                logger.LogInformation(
                    "Season pack S{Season}: importing {Kept} of {Total} mapped file(s) covering canonical episode(s) {Needed}",
                    season, mapped.Count, before, DescribeTargets(canonicalTargets));
            }

            if (mapped.Count == 0)
                throw new EpisodeMappingException($"Season pack S{season:D2} contained no confidently mapped episode files.");

            var inspected = await InspectSelectionAsync(job, mapped.Select(x => x.FilePath), allFiles, prefs, ct);
            foreach (var mappingGroup in GroupCanonicalMappings(mapped))
            {
                if (mappingGroup.Count > 1)
                {
                    records.Add(await JoinMultipartEpisodeAsync(job, mappingGroup, prefs,
                        inspected, ct));
                    continue;
                }
                var mapping = mappingGroup[0];
                var file = mapping.FilePath;
                var episodes = mapping.Coverage.Select(x => x.Episode).Distinct().OrderBy(x => x).ToList();
                var first = episodes[0];
                string dest;
                if (prefs.SplitSeasonPacks)
                {
                    if (episodes.Count == 1)
                    {
                        var title = await GetEpisodeTitleAsync(job, season, first, ct);
                        dest = naming.BuildEpisodePath(prefs, job, season, first, title, Path.GetExtension(file));
                    }
                    else
                        dest = naming.BuildEpisodeRangePath(prefs, job, season, first, episodes[^1],
                            Path.GetExtension(file));
                }
                else
                {
                    var folder = naming.BuildSeasonPackFolder(prefs, job, season);
                    var name = NamingTemplateEngine.SanitizeComponent(Path.GetFileNameWithoutExtension(file)) + Path.GetExtension(file);
                    dest = Path.Combine(folder, name);
                }

                var coverage = mapping.Coverage;
                TransferOne(file, dest, season, first, "video", records, prefs,
                    inspected.GetValueOrDefault(file), coverage, job);
                PairSubtitle(job, file, dest, allFiles, prefs, season, first, records, coverage);
            }
            return records;
        }

        // Whole-series / multi-season packs can use either a configured order or a distinctive canonical
        // season name repeated in each path. Both routes produce the same canonical mappings and must prove
        // the exact frozen target set again before the first library write.
        var wholePackTargets = CanonicalTargets(transfer);
        List<CanonicalFileMapping>? canonicalMappings = null;
        if (EpisodeOrderMapping.IsActive(job.EpisodeOrderProfile))
            canonicalMappings = MapTranslatedFiles(job, videoFiles);
        else if (job.IsAnime && wholePackTargets.Count > 0 && job.CanonicalSeasons.Count > 0)
            canonicalMappings = MapNamedCollectionFiles(job, videoFiles);

        if (canonicalMappings is not null)
        {
            if (wholePackTargets.Count > 0)
            {
                var before = canonicalMappings.Count;
                var mixed = canonicalMappings.Where(mapping =>
                        mapping.Coverage.Any(target => wholePackTargets.Contains((target.Season, target.Episode)))
                        && mapping.Coverage.Any(target => !wholePackTargets.Contains((target.Season, target.Episode))))
                    .ToList();
                if (mixed.Count > 0)
                    throw new EpisodeMappingException(
                        $"Cross-season pack combines requested and non-requested canonical episodes in {mixed.Count} file(s); no files were imported.");

                canonicalMappings = RestrictToCanonicalTargets(canonicalMappings, wholePackTargets);
                var covered = canonicalMappings.SelectMany(x => x.Coverage)
                    .Select(x => (x.Season, x.Episode)).ToHashSet();
                var missing = wholePackTargets.Where(x => !covered.Contains(x))
                    .OrderBy(x => x.Season).ThenBy(x => x.Episode).ToList();
                if (missing.Count > 0)
                    throw new EpisodeMappingException(
                        $"Cross-season pack cannot prove coverage for requested episode(s) {DescribeTargets(missing)}; no files were imported.");
                logger.LogInformation(
                    "Cross-season pack: importing {Kept} of {Total} mapped file(s) covering canonical episode(s) {Needed}",
                    canonicalMappings.Count, before, DescribeTargets(wholePackTargets));
            }
            var inspection = await InspectSelectionAsync(job, canonicalMappings.Select(x => x.FilePath), allFiles, prefs, ct);
            foreach (var mappingGroup in GroupCanonicalMappings(canonicalMappings))
            {
                if (mappingGroup.Count > 1)
                {
                    records.Add(await JoinMultipartEpisodeAsync(job, mappingGroup, prefs,
                        inspection, ct));
                    continue;
                }
                var mapping = mappingGroup[0];
                var file = mapping.FilePath;
                var canonicalSeason = mapping.Coverage[0].Season;
                var episodes = mapping.Coverage.Select(x => x.Episode).ToList();
                var first = episodes[0];
                var dest = episodes.Count == 1
                    ? naming.BuildEpisodePath(prefs, job, canonicalSeason, first,
                        await GetEpisodeTitleAsync(job, canonicalSeason, first, ct), Path.GetExtension(file))
                    : naming.BuildEpisodeRangePath(prefs, job, canonicalSeason, first, episodes[^1], Path.GetExtension(file));
                TransferOne(file, dest, canonicalSeason, first, "video", records, prefs,
                    inspection.GetValueOrDefault(file), mapping.Coverage, job);
                PairSubtitle(job, file, dest, allFiles, prefs, canonicalSeason, first, records, mapping.Coverage);
            }
            return records;
        }

        var mappedFiles = new List<EpisodeFileMapping>();
        var unmappedFiles = new List<string>();
        foreach (var file in videoFiles)
        {
            var parsed = parser.Parse(Path.GetFileName(file));
            var episodes = parsed.EpisodeNumbers.Distinct().OrderBy(x => x).ToList();
            if (parsed.Season is not int s || !IsContiguous(episodes))
            {
                unmappedFiles.Add(file);
                continue;
            }
            mappedFiles.Add(new EpisodeFileMapping(file, s, episodes));
        }
        var conflicts = mappedFiles.SelectMany(m => m.Episodes.Select(e => (m.Season, Episode: e)))
            .GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (unmappedFiles.Count > 0 || conflicts.Count > 0)
            throw new EpisodeMappingException(
                $"Whole-series pack mapping is ambiguous: {unmappedFiles.Count} unmapped file(s), {conflicts.Count} overlapping episode(s); no files were imported.");
        if (mappedFiles.Count == 0)
            throw new EpisodeMappingException("Whole-series pack contained no confidently mapped episode files.");

        var wholeSeriesInspection = await InspectSelectionAsync(job, mappedFiles.Select(x => x.FilePath),
            allFiles, prefs, ct);
        foreach (var mapping in mappedFiles)
        {
            var file = mapping.FilePath;
            var first = mapping.Episodes[0];
            string dest;
            if (mapping.Episodes.Count == 1)
            {
                var title = episodeTitles.GetEpisodeTitleAsync(job.TmdbId, mapping.Season, first,
                    CancellationToken.None).GetAwaiter().GetResult();
                dest = naming.BuildEpisodePath(prefs, job, mapping.Season, first, title, Path.GetExtension(file));
            }
            else
                dest = naming.BuildEpisodeRangePath(prefs, job, mapping.Season, first,
                    mapping.Episodes[^1], Path.GetExtension(file));
            var coverage = Coverage(mapping.Season, mapping.Episodes);
            TransferOne(file, dest, mapping.Season, first, "video", records, prefs,
                wholeSeriesInspection.GetValueOrDefault(file), coverage, job);
            PairSubtitle(job, file, dest, allFiles, prefs, mapping.Season, first, records, coverage);
        }
        return records;
    }

    private sealed record CanonicalFileMapping(string FilePath, IReadOnlyList<EpisodeRef> Coverage, int Part = 1);

    private static HashSet<(int Season, int Episode)> CanonicalTargets(TransferItem transfer)
    {
        if (transfer.NeededEpisodeRefs is { Count: > 0 })
            return transfer.NeededEpisodeRefs.Where(x => x.Season >= 0 && x.Episode > 0)
                .Select(x => (x.Season, x.Episode)).ToHashSet();
        return transfer.Season is int season && transfer.NeededEpisodes is { Count: > 0 }
            ? transfer.NeededEpisodes.Where(x => x > 0).Select(x => (season, x)).ToHashSet()
            : new();
    }

    private static List<CanonicalFileMapping> RestrictToCanonicalTargets(
        IEnumerable<CanonicalFileMapping> mappings, HashSet<(int Season, int Episode)> targets) =>
        mappings.Where(m => m.Coverage.Any(x => targets.Contains((x.Season, x.Episode)))).ToList();

    private static string DescribeTargets(IEnumerable<(int Season, int Episode)> targets) =>
        string.Join(",", targets.OrderBy(x => x.Season).ThenBy(x => x.Episode)
            .Select(x => $"S{x.Season:D2}E{x.Episode:D2}"));

    private List<CanonicalFileMapping> MapTranslatedFiles(FulfillmentJobDto job, IReadOnlyList<string> videoFiles)
    {
        var mapped = new List<CanonicalFileMapping>();
        var unmapped = new List<string>();
        foreach (var file in videoFiles)
        {
            var parsed = parser.Parse(Path.GetFileName(file));
            var sourceEpisodes = parsed.EpisodeNumbers.Distinct().OrderBy(x => x).ToList();
            if (parsed.Season is not int sourceSeason || sourceEpisodes.Count == 0 || !IsContiguous(sourceEpisodes))
            {
                unmapped.Add(file);
                continue;
            }

            var coverage = new List<EpisodeRef>();
            var parts = new List<int>();
            foreach (var sourceEpisode in sourceEpisodes)
            {
                if (!EpisodeOrderMapping.TryTranslateFileDetailed(job.EpisodeOrderProfile, file,
                        sourceSeason, sourceEpisode, out var destination))
                {
                    coverage.Clear();
                    break;
                }
                coverage.Add(destination.Episode);
                parts.Add(destination.Part);
            }

            coverage = coverage.DistinctBy(x => (x.Season, x.Episode))
                .OrderBy(x => x.Season).ThenBy(x => x.Episode).ToList();
            if (coverage.Count == 0 || parts.Distinct().Count() != 1
                || coverage.Any(x => x.Season != coverage[0].Season)
                || !IsContiguous(coverage.Select(x => x.Episode).ToList()))
                unmapped.Add(file);
            else
                mapped.Add(new CanonicalFileMapping(file, coverage, parts[0]));
        }

        var conflicts = mapped.SelectMany(mapping => mapping.Coverage.Select(target => (mapping, target)))
            .GroupBy(item => (item.target.Season, item.target.Episode))
            .Where(group => !ValidMultipartGroup(job.EpisodeOrderProfile, group.Key,
                group.Select(item => item.mapping).ToList()))
            .Select(group => group.Key).ToList();
        if (unmapped.Count > 0 || conflicts.Count > 0)
            throw new EpisodeMappingException(
                $"Configured episode order could not map the pack safely: {unmapped.Count} unmapped file(s), {conflicts.Count} overlapping canonical episode(s); no files were imported.");
        if (mapped.Count == 0)
            throw new EpisodeMappingException("Configured episode order produced no canonical episode files.");
        return mapped;
    }

    private static bool ValidMultipartGroup(SeriesEpisodeOrderProfileDto? profile,
        (int Season, int Episode) target, IReadOnlyList<CanonicalFileMapping> mappings)
    {
        if (!EpisodeOrderMapping.HasCustomMetadata(profile) || mappings.Count < 2
            || mappings.Any(mapping => mapping.Coverage.Count != 1)) return mappings.Count == 1;
        var actual = mappings.Select(mapping => mapping.Part).Order().ToList();
        if (!EpisodeOrderMapping.TryParseDetailed(profile!, out var configured, out _)) return false;
        var expected = configured.Values.Where(value => value.Episode.Season == target.Season
                                                        && value.Episode.Episode == target.Episode)
            .Select(value => value.Part).Order().ToList();
        return actual.SequenceEqual(expected);
    }

    private List<CanonicalFileMapping> MapNamedCollectionFiles(
        FulfillmentJobDto job, IReadOnlyList<string> videoFiles)
    {
        var mapped = new List<CanonicalFileMapping>();
        var unmapped = new List<string>();
        foreach (var file in videoFiles)
        {
            var parsed = parser.Parse(Path.GetFileName(file));
            if (!AnimeNamedCollectionMapper.TryMapFile(file, job, parsed, out var coverage)
                || coverage.Count == 0
                || coverage.Any(target => target.Season != coverage[0].Season)
                || !IsContiguous(coverage.Select(target => target.Episode).ToList()))
            {
                unmapped.Add(file);
                continue;
            }
            mapped.Add(new CanonicalFileMapping(file, coverage));
        }

        var conflicts = mapped.SelectMany(mapping => mapping.Coverage)
            .GroupBy(target => (target.Season, target.Episode))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (unmapped.Count > 0 || conflicts.Count > 0)
            throw new EpisodeMappingException(
                $"Named anime collection mapping is ambiguous: {unmapped.Count} unmapped file(s), " +
                $"{conflicts.Count} overlapping canonical episode(s); no files were imported.");
        if (mapped.Count == 0)
            throw new EpisodeMappingException(
                "Named anime collection contained no confidently mapped canonical episode files.");
        return mapped;
    }

    private List<CanonicalFileMapping> MapFractionalNamedSeasonFiles(
        FulfillmentJobDto job,
        TransferItem transfer,
        IReadOnlyList<string> videoFiles,
        int? expectedEpisodeCount,
        int insertionAfter)
    {
        if (transfer.SourceSeason is not int sourceSeason
            || transfer.Season is not int canonicalSeason
            || expectedEpisodeCount is not int episodeCount)
            throw new EpisodeMappingException(
                "Fractional named-season transfer is missing its frozen source/canonical episode-count contract; no files were imported.");

        var mapped = new List<CanonicalFileMapping>();
        var unmapped = new List<string>();
        foreach (var file in videoFiles)
        {
            if (!AnimeNamedSeasonSequenceMapper.TryMapFile(file, job, sourceSeason, canonicalSeason,
                    episodeCount, insertionAfter, parser, out var canonicalEpisode))
            {
                unmapped.Add(file);
                continue;
            }
            mapped.Add(new CanonicalFileMapping(file,
                [new EpisodeRef { Season = canonicalSeason, Episode = canonicalEpisode }]));
        }

        var conflicts = mapped.GroupBy(mapping =>
                (mapping.Coverage[0].Season, mapping.Coverage[0].Episode))
            .Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        if (unmapped.Count > 0 || conflicts.Count > 0)
            throw new EpisodeMappingException(
                $"Fractional named-season mapping is ambiguous: {unmapped.Count} unmapped file(s), " +
                $"{conflicts.Count} overlapping canonical episode(s); no files were imported.");
        if (mapped.Count == 0)
            throw new EpisodeMappingException(
                "Fractional named-season transfer contained no confidently mapped episode files.");
        return mapped;
    }

    private static IReadOnlyList<List<CanonicalFileMapping>> GroupCanonicalMappings(
        IReadOnlyList<CanonicalFileMapping> mappings) => mappings
        .GroupBy(mapping => (mapping.Coverage[0].Season, mapping.Coverage[0].Episode))
        .OrderBy(group => group.Key.Season).ThenBy(group => group.Key.Episode)
        .Select(group => group.OrderBy(mapping => mapping.Part).ToList()).ToList();

    private async Task<string?> GetEpisodeTitleAsync(FulfillmentJobDto job, int season, int episode,
        CancellationToken ct)
    {
        var custom = job.EpisodeOrderProfile?.CustomEpisodes
            .FirstOrDefault(row => row.Season == season && row.Episode == episode
                                   && !string.IsNullOrWhiteSpace(row.Title));
        return custom?.Title.Trim()
               ?? await episodeTitles.GetEpisodeTitleAsync(job.TmdbId, season, episode, ct);
    }

    /// <summary>
    /// Joins an explicitly configured split broadcast episode into one Plex file. Plex has limited pt1/pt2
    /// support, but joining preserves intro detection and stream selection across clients. The output is
    /// built beside its destination and atomically renamed; every torrent source stays untouched until the
    /// joined file and playback defaults have both been verified.
    /// </summary>
    private async Task<ImportedFileRecord> JoinMultipartEpisodeAsync(FulfillmentJobDto job,
        IReadOnlyList<CanonicalFileMapping> mappings, EffectiveLibraryOrganization prefs,
        IReadOnlyDictionary<string, MediaTrackSummaryDto> inspections,
        CancellationToken ct)
    {
        if (mappings.Count is < 2 or > 8 || mappings.Any(mapping => mapping.Coverage.Count != 1))
            throw new EpisodeMappingException("A split episode must contain 2 through 8 ordered one-episode parts.");
        var target = mappings[0].Coverage[0];
        if (mappings.Any(mapping => mapping.Coverage[0].Season != target.Season
                                    || mapping.Coverage[0].Episode != target.Episode)
            || !mappings.Select(mapping => mapping.Part).SequenceEqual(Enumerable.Range(1, mappings.Count)))
            throw new EpisodeMappingException(
                $"S{target.Season:D2}E{target.Episode:D2} has an incomplete or out-of-order split-part contract.");
        if (mappings.Any(mapping => !Path.GetExtension(mapping.FilePath).Equals(".mkv",
                StringComparison.OrdinalIgnoreCase)))
            throw new EpisodeMappingException(
                $"S{target.Season:D2}E{target.Episode:D2} split parts must all be MKV files for lossless joining.");

        var summaries = mappings.Select(mapping => inspections.GetValueOrDefault(mapping.FilePath)
                                                    ?? throw new MediaPolicyViolationException(
                                                        $"No media inspection exists for '{Path.GetFileName(mapping.FilePath)}'."))
            .ToList();
        if (summaries.Any(summary => summary.Audio.Concat(summary.Subtitles).Any(track => track.IsExternal)))
            throw new EpisodeMappingException(
                $"S{target.Season:D2}E{target.Episode:D2} has external subtitles across split parts; join them manually before import.");
        var contracts = summaries.Select(TrackContract).Distinct(StringComparer.Ordinal).ToList();
        if (contracts.Count != 1)
            throw new EpisodeMappingException(
                $"S{target.Season:D2}E{target.Episode:D2} split parts do not have identical audio/subtitle stream layouts.");

        var title = await GetEpisodeTitleAsync(job, target.Season, target.Episode, ct);
        var destination = naming.BuildEpisodePath(prefs, job, target.Season, target.Episode, title, ".mkv");
        var summary = summaries[0];
        var selection = MediaTrackDefaultSelection.Create(job.MediaLanguagePolicy, summary, job.IsAnime);
        var defaultsApplied = false;
        Action<string>? prepare = selection is null ? null : staged =>
            defaultsApplied = trackInspector.SetDefaults(staged, ".mkv", selection);
        var size = await multipartJoiner.JoinAsync(mappings.Select(mapping => mapping.FilePath).ToList(),
            destination, prepare, ct);
        if (defaultsApplied && selection is not null) RecordAppliedDefaults(summary, selection);

        if (prefs.TransferMode == TransferMode.Move
            || prefs.TransferMode == TransferMode.Copy && prefs.DeleteSourceAfterImport)
            foreach (var mapping in mappings)
                TryDeleteJoinedSource(mapping.FilePath);

        logger.LogInformation("Joined {Count} source part(s) into canonical S{Season:D2}E{Episode:D2}",
            mappings.Count, target.Season, target.Episode);
        return new ImportedFileRecord(mappings[0].FilePath, destination, "video", target.Season,
            target.Episode, size, summary,
            [new EpisodeRef { Season = target.Season, Episode = target.Episode }]);
    }

    private static string TrackContract(MediaTrackSummaryDto summary) => string.Join('|',
        summary.Audio.Concat(summary.Subtitles).Where(track => !track.IsExternal)
            // Track titles can legitimately describe the individual half while codec, language, type and
            // order remain append-compatible. Do not reject that harmless metadata difference.
            .Select(track => $"{track.Type}:{track.Codec}:{MediaLanguagePolicy.Normalize(track.Language)}"));

    private void TryDeleteJoinedSource(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { logger.LogDebug(ex, "Could not delete joined source part {Path}", path); }
    }

    private void TransferOne(string source, string dest, int? season, int? episode, string fileType,
        List<ImportedFileRecord> records, EffectiveLibraryOrganization prefs,
        MediaTrackSummaryDto? mediaTracks = null, IReadOnlyList<EpisodeRef>? episodeCoverage = null,
        FulfillmentJobDto? playbackJob = null)
    {
        var selection = mediaTracks is null || playbackJob is null
            ? null
            : MediaTrackDefaultSelection.Create(playbackJob.MediaLanguagePolicy, mediaTracks, playbackJob.IsAnime);
        var defaultsApplied = false;
        Action<string>? prepare = selection is null ? null : staged =>
            defaultsApplied = trackInspector.SetDefaults(staged, Path.GetExtension(source), selection);
        FileTransfer.Transfer(source, dest, prefs.TransferMode, prefs.DeleteSourceAfterImport, logger, prepare);
        if (defaultsApplied && mediaTracks is not null && selection is not null)
            RecordAppliedDefaults(mediaTracks, selection);
        records.Add(new ImportedFileRecord(source, dest, fileType, season, episode, SafeLength(dest), mediaTracks,
            episodeCoverage));
    }

    private static void RecordAppliedDefaults(MediaTrackSummaryDto tracks, MediaTrackDefaultSelection selection)
    {
        tracks.Audio = RecordAppliedOrder(tracks.Audio, selection.AudioOrdinal,
            editDefaults: selection.AudioOrdinal is not null);
        if (selection.EditSubtitles)
            tracks.Subtitles = RecordAppliedOrder(tracks.Subtitles, selection.SubtitleOrdinal,
                editDefaults: true);
    }

    private static List<MediaTrackDto> RecordAppliedOrder(IReadOnlyList<MediaTrackDto> tracks,
        int? selectedOrdinal, bool editDefaults)
    {
        var embedded = tracks.Where(track => !track.IsExternal).ToList();
        var external = tracks.Where(track => track.IsExternal).ToList();
        MediaTrackDto? selected = null;
        if (selectedOrdinal is int ordinal && ordinal >= 1 && ordinal <= embedded.Count)
        {
            selected = embedded[ordinal - 1];
            embedded.RemoveAt(ordinal - 1);
            embedded.Insert(0, selected);
        }

        for (var index = 0; index < embedded.Count; index++)
        {
            embedded[index].Index = index + 1;
            if (editDefaults) embedded[index].IsDefault = ReferenceEquals(embedded[index], selected);
        }
        return embedded.Concat(external).ToList();
    }

    private async Task<Dictionary<string, MediaTrackSummaryDto>> InspectSelectionAsync(FulfillmentJobDto job,
        IEnumerable<string> selectedFiles, IReadOnlyList<string> allFiles, EffectiveLibraryOrganization prefs,
        CancellationToken ct)
    {
        var results = new Dictionary<string, MediaTrackSummaryDto>(StringComparer.OrdinalIgnoreCase);
        var selected = selectedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var file in selected)
        {
            MediaTrackSummaryDto tracks;
            try
            {
                var companions = ShouldKeepSubtitles(job, prefs) ? CompanionSubtitles(file, allFiles, prefs) : [];
                tracks = await trackInspector.InspectAsync(file, companions, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new MediaPolicyViolationException(
                    $"Could not verify media tracks for '{Path.GetFileName(file)}': {ex.Message}", ex);
            }

            if (!tracks.HasVideo)
                throw new MediaPolicyViolationException(
                    $"'{Path.GetFileName(file)}' contains no readable video stream and appears corrupt or incomplete");
            if (job.StorageOptimizationPolicy is { } optimization)
            {
                var video = tracks.Video.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(optimization.RequiredVideoCodec)
                    && !VideoCodecPolicy.Matches(video?.Codec, optimization.RequiredVideoCodec))
                    throw new MediaPolicyViolationException(
                        $"'{Path.GetFileName(file)}' contains {VideoCodecPolicy.Display(video?.Codec)}, not required " +
                        VideoCodecPolicy.Display(optimization.RequiredVideoCodec));

                var observedQuality = VideoResolutionPolicy.FromDimensions(video?.Width, video?.Height);
                if (optimization.TargetQuality != Quality.Any
                    && observedQuality != optimization.TargetQuality)
                    throw new MediaPolicyViolationException(
                        $"'{Path.GetFileName(file)}' is {observedQuality.Label()}, not the " +
                        $"{optimization.TargetQuality.Label()} optimization target");
                if (optimization.TargetQuality == Quality.Any && job.Quality != Quality.Any
                    && observedQuality < job.Quality)
                    throw new MediaPolicyViolationException(
                        $"'{Path.GetFileName(file)}' would lower the selected media below {job.Quality.Label()}");
            }
            var decision = MediaLanguagePolicy.Evaluate(job.MediaLanguagePolicy, tracks, job.IsAnime);
            if (!decision.Accepted)
                throw new MediaPolicyViolationException(
                    $"'{Path.GetFileName(file)}' failed the '{job.QualityProfile?.Name ?? "selected"}' media policy: {decision.Reason}");
            results[file] = tracks;
        }

        if (job.StorageOptimizationPolicy is { MinimumSavingsPercent: > 0 } policy
            && (job.MediaType == MediaType.Movie || policy.Targets.Count == 1 || selected.Count > 1))
        {
            var replacementBytes = selected.Sum(SafeLength);
            if (replacementBytes <= 0 || replacementBytes > policy.MaximumReplacementBytes)
                throw new MediaPolicyViolationException(
                    $"Selected replacement payload is {replacementBytes / 1_000_000_000d:F2} GB; it does not prove " +
                    $"the requested {policy.MinimumSavingsPercent}% saving from {policy.CurrentBytes / 1_000_000_000d:F2} GB");
        }
        return results;
    }

    private static List<string> CompanionSubtitles(string video, IReadOnlyList<string> allFiles,
        EffectiveLibraryOrganization prefs)
    {
        var directory = Path.GetDirectoryName(video) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(video);
        var subtitles = allFiles.Where(file =>
                string.Equals(Path.GetDirectoryName(file) ?? string.Empty, directory,
                    StringComparison.OrdinalIgnoreCase)
                && prefs.SubtitleExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .ToList();
        var matches = subtitles.Where(file =>
            {
                var subtitleStem = Path.GetFileNameWithoutExtension(file);
                return subtitleStem.Equals(stem, StringComparison.OrdinalIgnoreCase)
                       || subtitleStem.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase);
            }).ToList();
        return matches.Count > 0 ? matches : subtitles.Count == 1 ? subtitles : [];
    }

    private void PairSubtitle(FulfillmentJobDto job, string videoSource, string videoDest, List<string> allFiles,
        EffectiveLibraryOrganization prefs, int? season, int? episode, List<ImportedFileRecord> records,
        IReadOnlyList<EpisodeRef>? episodeCoverage = null)
    {
        if (!ShouldKeepSubtitles(job, prefs)) return;

        var videoStem = Path.GetFileNameWithoutExtension(videoSource);
        foreach (var match in CompanionSubtitles(videoSource, allFiles, prefs))
        {
            var subStem = Path.GetFileNameWithoutExtension(match);
            var infix = subStem.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase)
                ? subStem[videoStem.Length..]
                : string.Empty;
            var subDestName = Path.GetFileNameWithoutExtension(videoDest) + infix + Path.GetExtension(match);
            var subDest = Path.Combine(Path.GetDirectoryName(videoDest) ?? string.Empty, subDestName);
            TransferOne(match, subDest, season, episode, "subtitle", records, prefs,
                episodeCoverage: episodeCoverage);
        }
    }

    private static bool ShouldKeepSubtitles(FulfillmentJobDto job, EffectiveLibraryOrganization prefs) =>
        prefs.KeepSubtitles || job.MediaLanguagePolicy is
            { RequiredSubtitleLanguages.Count: > 0 }
            or { RequireForcedSubtitle: true }
            or { PreferForcedSubtitles: true }
            or { Preference: ReleaseLanguagePreference.OriginalWithEnglishSubtitles }
            || job is { IsAnime: true, MediaLanguagePolicy.Preference: ReleaseLanguagePreference.Smart };

    private static List<EpisodeRef> Coverage(int season, IEnumerable<int> episodes) => episodes
        .Distinct().OrderBy(x => x).Select(x => new EpisodeRef { Season = season, Episode = x }).ToList();

    private static bool IsContiguous(IReadOnlyList<int> episodes) => episodes.Count > 0
        && episodes.SequenceEqual(Enumerable.Range(episodes[0], episodes.Count));

    private static string DescribeMappingFailure(int season, SeasonPackMapResult map)
    {
        var details = new List<string>();
        if (map.UnmappedFiles.Count > 0)
            details.Add($"{map.UnmappedFiles.Count} file(s) had no valid explicit episode identity");
        if (map.ConflictingEpisodes.Count > 0)
            details.Add($"episode(s) {string.Join(",", map.ConflictingEpisodes)} were claimed by multiple files");
        return $"Season pack S{season:D2} mapping is ambiguous ({string.Join("; ", details)}); no files were imported.";
    }

    private static Func<string, bool> IsVideo(EffectiveLibraryOrganization prefs) => f =>
        prefs.VideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) &&
        !Path.GetFileName(f).Contains("sample", StringComparison.OrdinalIgnoreCase);

    private static Func<string, bool> IsAudio(EffectiveLibraryOrganization prefs) => f =>
        prefs.AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) &&
        !Path.GetFileName(f).Contains("sample", StringComparison.OrdinalIgnoreCase);

    private static bool PassesMinVideoSize(string file, EffectiveLibraryOrganization prefs) =>
        SafeLength(file) >= prefs.MinVideoFileSizeMb * 1024 * 1024;

    private static bool PassesMinAudioSize(string file, EffectiveLibraryOrganization prefs) =>
        SafeLength(file) >= prefs.MinAudioFileSizeMb * 1024 * 1024;

    private static string CleanLabel(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static bool ContainsNormalized(string value, string wanted)
    {
        static string N(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var needle = N(wanted);
        return needle.Length > 1 && N(value).Contains(needle, StringComparison.Ordinal);
    }

    private static (int? Disc, int? Track) ParseTrackNumbers(string file)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(file));
        var discMatch = Regex.Match(parent ?? string.Empty, @"(?:cd|disc)\s*(\d+)", RegexOptions.IgnoreCase);
        var match = Regex.Match(Path.GetFileNameWithoutExtension(file), @"^\s*(?:(\d{1,2})[-_.])?(\d{1,3})(?:\D|$)");
        int? disc = discMatch.Success ? int.Parse(discMatch.Groups[1].Value)
            : match.Success && match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : null;
        int? track = match.Success ? int.Parse(match.Groups[2].Value) : null;
        return (disc, track);
    }

    private static string NaturalTrackKey(string file)
    {
        var parsed = ParseTrackNumbers(file);
        return $"{parsed.Disc ?? 1:D3}-{parsed.Track ?? 999:D4}-{file}";
    }

    private static string StripTrackPrefix(string stem) =>
        Regex.Replace(stem, @"^\s*(?:\d{1,2}[-_.])?\d{1,3}\s*[-_. ]+", string.Empty).Trim();

    private static bool IsPreferredArtwork(string file)
    {
        var ext = Path.GetExtension(file);
        if (ext is not (".jpg" or ".jpeg" or ".png")) return false;
        var name = Path.GetFileNameWithoutExtension(file);
        return name.Equals("cover", StringComparison.OrdinalIgnoreCase)
               || name.Equals("folder", StringComparison.OrdinalIgnoreCase)
               || name.Equals("front", StringComparison.OrdinalIgnoreCase);
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; }
        catch { return 0; }
    }

    private sealed class MediaPolicyViolationException(string message, Exception? inner = null)
        : Exception(message, inner);

    private sealed class EpisodeMappingException(string message) : Exception(message);
}
