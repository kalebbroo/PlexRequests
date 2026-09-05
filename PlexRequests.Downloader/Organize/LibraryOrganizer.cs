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
                var parsed = parser.Parse(Path.GetFileName(best));
                var sourceEpisodes = parsed.EpisodeNumbers.Distinct().ToList();
                if (parsed.Season is not int sourceSeason || sourceEpisodes.Count != 1
                    || !EpisodeOrderMapping.TryTranslate(job.EpisodeOrderProfile, sourceSeason,
                        sourceEpisodes[0], out var canonical)
                    || canonical.Season != s || canonical.Episode != e)
                    throw new EpisodeMappingException(
                        $"Standalone file '{Path.GetFileName(best)}' does not prove canonical identity S{s:D2}E{e:D2}; no files were imported.");
            }

            var inspected = await InspectSelectionAsync(job, [best], allFiles, prefs, ct);
            var title = episodeTitles.GetEpisodeTitleAsync(job.TmdbId, s, e, CancellationToken.None).GetAwaiter().GetResult();
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
            if (EpisodeOrderMapping.IsActive(job.EpisodeOrderProfile))
                mapped = MapTranslatedFiles(job, videoFiles);
            else
            {
                var map = splitter.Map(videoFiles, season, expectedEpisodeCount);
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
            foreach (var mapping in mapped)
            {
                var file = mapping.FilePath;
                var episodes = mapping.Coverage.Select(x => x.Episode).Distinct().OrderBy(x => x).ToList();
                var first = episodes[0];
                string dest;
                if (prefs.SplitSeasonPacks)
                {
                    if (episodes.Count == 1)
                    {
                        var title = episodeTitles.GetEpisodeTitleAsync(job.TmdbId, season, first,
                            CancellationToken.None).GetAwaiter().GetResult();
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

        // Whole-series / multi-season pack with no single target season (e.g. metadata was unavailable
        // at enqueue time): parse each file's own season+episode coverage independently. One unmapped or
        // overlapping file rejects the import — never guess or silently import a partial series.
        if (EpisodeOrderMapping.IsActive(job.EpisodeOrderProfile))
        {
            var translated = MapTranslatedFiles(job, videoFiles);
            var canonicalTargets = CanonicalTargets(transfer);
            if (canonicalTargets.Count > 0)
            {
                var before = translated.Count;
                translated = RestrictToCanonicalTargets(translated, canonicalTargets);
                var covered = translated.SelectMany(x => x.Coverage)
                    .Select(x => (x.Season, x.Episode)).ToHashSet();
                var missing = canonicalTargets.Where(x => !covered.Contains(x))
                    .OrderBy(x => x.Season).ThenBy(x => x.Episode).ToList();
                if (missing.Count > 0)
                    throw new EpisodeMappingException(
                        $"Cross-season pack cannot prove coverage for requested episode(s) {DescribeTargets(missing)}; no files were imported.");
                logger.LogInformation(
                    "Cross-season pack: importing {Kept} of {Total} mapped file(s) covering canonical episode(s) {Needed}",
                    translated.Count, before, DescribeTargets(canonicalTargets));
            }
            var inspection = await InspectSelectionAsync(job, translated.Select(x => x.FilePath), allFiles, prefs, ct);
            foreach (var mapping in translated)
            {
                var file = mapping.FilePath;
                var canonicalSeason = mapping.Coverage[0].Season;
                var episodes = mapping.Coverage.Select(x => x.Episode).ToList();
                var first = episodes[0];
                var dest = episodes.Count == 1
                    ? naming.BuildEpisodePath(prefs, job, canonicalSeason, first,
                        episodeTitles.GetEpisodeTitleAsync(job.TmdbId, canonicalSeason, first, CancellationToken.None).GetAwaiter().GetResult(),
                        Path.GetExtension(file))
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

    private sealed record CanonicalFileMapping(string FilePath, IReadOnlyList<EpisodeRef> Coverage);

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
            foreach (var sourceEpisode in sourceEpisodes)
            {
                if (!EpisodeOrderMapping.TryTranslate(job.EpisodeOrderProfile, sourceSeason, sourceEpisode, out var target))
                {
                    coverage.Clear();
                    break;
                }
                coverage.Add(target);
            }

            coverage = coverage.DistinctBy(x => (x.Season, x.Episode))
                .OrderBy(x => x.Season).ThenBy(x => x.Episode).ToList();
            if (coverage.Count == 0 || coverage.Any(x => x.Season != coverage[0].Season)
                || !IsContiguous(coverage.Select(x => x.Episode).ToList()))
                unmapped.Add(file);
            else
                mapped.Add(new CanonicalFileMapping(file, coverage));
        }

        var conflicts = mapped.SelectMany(x => x.Coverage)
            .GroupBy(x => (x.Season, x.Episode)).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
        if (unmapped.Count > 0 || conflicts.Count > 0)
            throw new EpisodeMappingException(
                $"Configured episode order could not map the pack safely: {unmapped.Count} unmapped file(s), {conflicts.Count} overlapping canonical episode(s); no files were imported.");
        if (mapped.Count == 0)
            throw new EpisodeMappingException("Configured episode order produced no canonical episode files.");
        return mapped;
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

        foreach (var file in selectedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
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
            var decision = MediaLanguagePolicy.Evaluate(job.MediaLanguagePolicy, tracks);
            if (!decision.Accepted)
                throw new MediaPolicyViolationException(
                    $"'{Path.GetFileName(file)}' failed the '{job.QualityProfile?.Name ?? "selected"}' media policy: {decision.Reason}");
            results[file] = tracks;
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
            or { Preference: ReleaseLanguagePreference.OriginalWithEnglishSubtitles };

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
