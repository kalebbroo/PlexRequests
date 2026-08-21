using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Ranking;
using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
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
                MediaType.Movie => OrganizeMovie(job, mediaFiles, files, prefs),
                MediaType.Music => OrganizeMusic(job, contentRoot, mediaFiles, files, prefs),
                _ => OrganizeTv(job, transfer, mediaFiles, files, prefs, await ExpectedEpisodeCountAsync(job, transfer, ct))
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

    private List<ImportedFileRecord> OrganizeMovie(FulfillmentJobDto job, List<string> videoFiles, List<string> allFiles, EffectiveLibraryOrganization prefs)
    {
        var records = new List<ImportedFileRecord>();
        var best = videoFiles.OrderByDescending(f => SafeLength(f)).FirstOrDefault();
        if (best is null) return records;

        var dest = naming.BuildMoviePath(prefs, job, Path.GetExtension(best));
        TransferOne(best, dest, null, null, "video", records, prefs);
        PairSubtitle(best, dest, allFiles, prefs, null, null, records);
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

    private List<ImportedFileRecord> OrganizeTv(FulfillmentJobDto job, TransferItem transfer, List<string> videoFiles, List<string> allFiles, EffectiveLibraryOrganization prefs, int? expectedEpisodeCount)
    {
        var records = new List<ImportedFileRecord>();

        if (!transfer.IsPack)
        {
            // Single-episode item — pick the largest video file (packs sometimes bundle a sample/extra
            // alongside the real episode even when it's not nominally a "pack").
            var best = videoFiles.OrderByDescending(f => SafeLength(f)).FirstOrDefault();
            if (best is null || transfer.Season is not int s || transfer.Episode is not int e) return records;

            var title = episodeTitles.GetEpisodeTitleAsync(job.TmdbId, s, e, CancellationToken.None).GetAwaiter().GetResult();
            var dest = naming.BuildEpisodePath(prefs, job, s, e, title, Path.GetExtension(best));
            TransferOne(best, dest, s, e, "video", records, prefs);
            PairSubtitle(best, dest, allFiles, prefs, s, e, records);
            return records;
        }

        if (transfer.Season is int season)
        {
            if (prefs.SplitSeasonPacks)
            {
                var mapped = splitter.Map(videoFiles, season, expectedEpisodeCount);
                // If this pack was chosen to satisfy only specific episodes (an episode-level request, or a
                // partially-missing season), import just those — don't re-place episodes Plex already has.
                if (transfer.NeededEpisodes is { Count: > 0 } needed)
                {
                    var keep = needed.ToHashSet();
                    var before = mapped.Count;
                    var restricted = mapped.Where(m => keep.Contains(m.Episode)).ToList();
                    // Safety valve: if the requested episodes don't match any mapped file (TMDB vs the pack's
                    // internal numbering disagree — common for kids'/preschool shows), import everything the
                    // pack contains rather than nothing, so the content still lands.
                    if (restricted.Count == 0 && before > 0)
                        logger.LogWarning("Season pack S{Season}: none of the requested episode(s) {Needed} matched the pack's {Total} mapped file(s) (numbering mismatch?) — importing the whole pack",
                            season, string.Join(",", needed), before);
                    else
                    {
                        mapped = restricted;
                        logger.LogInformation("Season pack S{Season}: importing {Kept} of {Total} mapped episode(s) (restricted to requested episode(s) {Needed})",
                            season, mapped.Count, before, string.Join(",", needed));
                    }
                }
                foreach (var (file, episode) in mapped)
                {
                    var title = episodeTitles.GetEpisodeTitleAsync(job.TmdbId, season, episode, CancellationToken.None).GetAwaiter().GetResult();
                    var dest = naming.BuildEpisodePath(prefs, job, season, episode, title, Path.GetExtension(file));
                    TransferOne(file, dest, season, episode, "video", records, prefs);
                    PairSubtitle(file, dest, allFiles, prefs, season, episode, records);
                }
            }
            else
            {
                var folder = naming.BuildSeasonPackFolder(prefs, job, season);
                foreach (var file in videoFiles)
                {
                    var name = NamingTemplateEngine.SanitizeComponent(Path.GetFileNameWithoutExtension(file)) + Path.GetExtension(file);
                    var dest = Path.Combine(folder, name);
                    TransferOne(file, dest, season, null, "video", records, prefs);
                    PairSubtitle(file, dest, allFiles, prefs, season, null, records);
                }
            }
            return records;
        }

        // Whole-series / multi-season pack with no single target season (e.g. metadata was unavailable
        // at enqueue time): fall back to parsing each file's own season+episode independently. Files that
        // don't parse cleanly are skipped — never guessed — same "no confident mapping, don't import it
        // under a wrong name" principle as the splitter above.
        foreach (var file in videoFiles)
        {
            var parsed = parser.Parse(Path.GetFileName(file));
            if (parsed.Season is not int s || parsed.Episode is not int e)
            {
                logger.LogWarning("\"{Title}\": could not determine season/episode for \"{File}\" in a whole-series pack; skipped", job.Title, Path.GetFileName(file));
                continue;
            }
            var title = episodeTitles.GetEpisodeTitleAsync(job.TmdbId, s, e, CancellationToken.None).GetAwaiter().GetResult();
            var dest = naming.BuildEpisodePath(prefs, job, s, e, title, Path.GetExtension(file));
            TransferOne(file, dest, s, e, "video", records, prefs);
            PairSubtitle(file, dest, allFiles, prefs, s, e, records);
        }
        return records;
    }

    private void TransferOne(string source, string dest, int? season, int? episode, string fileType, List<ImportedFileRecord> records, EffectiveLibraryOrganization prefs)
    {
        var size = SafeLength(source);
        FileTransfer.Transfer(source, dest, prefs.TransferMode, prefs.DeleteSourceAfterImport, logger);
        records.Add(new ImportedFileRecord(source, dest, fileType, season, episode, size));
    }

    private void PairSubtitle(string videoSource, string videoDest, List<string> allFiles, EffectiveLibraryOrganization prefs, int? season, int? episode, List<ImportedFileRecord> records)
    {
        if (!prefs.KeepSubtitles) return;

        var videoDir = Path.GetDirectoryName(videoSource) ?? string.Empty;
        var videoStem = Path.GetFileNameWithoutExtension(videoSource);
        var subtitlesInDir = allFiles
            .Where(f => string.Equals(Path.GetDirectoryName(f), videoDir, StringComparison.OrdinalIgnoreCase))
            .Where(f => prefs.SubtitleExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();

        var match = subtitlesInDir.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).StartsWith(videoStem, StringComparison.OrdinalIgnoreCase))
                    ?? (subtitlesInDir.Count == 1 ? subtitlesInDir[0] : null);
        if (match is null) return;

        var subStem = Path.GetFileNameWithoutExtension(match);
        var infix = subStem.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase) ? subStem[videoStem.Length..] : string.Empty;
        var subDestName = Path.GetFileNameWithoutExtension(videoDest) + infix + Path.GetExtension(match);
        var subDest = Path.Combine(Path.GetDirectoryName(videoDest) ?? string.Empty, subDestName);
        TransferOne(match, subDest, season, episode, "subtitle", records, prefs);
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
}
