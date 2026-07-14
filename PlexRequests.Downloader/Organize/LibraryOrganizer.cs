using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Ranking;
using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Organize;

public interface ILibraryOrganizer
{
    Task<ImportResult> OrganizeAsync(FulfillmentJobDto job, TorrentItem torrent, string sourcePath, EffectiveLibraryOrganization prefs, CancellationToken ct);
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
    public async Task<ImportResult> OrganizeAsync(FulfillmentJobDto job, TorrentItem torrent, string sourcePath, EffectiveLibraryOrganization prefs, CancellationToken ct)
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
                stagingRoot = Path.Combine(parent ?? Path.GetTempPath(), ".plexrequests-staging", $"{job.Id}-{torrent.TorrentId}");
                foreach (var archive in archiveEntryPoints)
                    await extractor.ExtractAsync(archive, stagingRoot, ct);
                files = Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories).ToList();
            }

            var videoFiles = files.Where(IsVideo(prefs)).Where(f => PassesMinSize(f, prefs)).ToList();

            var records = job.MediaType == MediaType.Movie
                ? OrganizeMovie(job, videoFiles, files, prefs)
                : OrganizeTv(job, torrent, videoFiles, files, prefs, await ExpectedEpisodeCountAsync(job, torrent, ct));

            if (records.Count(r => r.FileType == "video") == 0)
            {
                var reason = archiveEntryPoints.Count > 0 && !prefs.ExtractArchives
                    ? $"No video files found for \"{job.Title}\" — source appears to be an archive but archive extraction is disabled"
                    : $"No video files found for \"{job.Title}\" after import (checked {files.Count} file(s))";
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

    private async Task<int?> ExpectedEpisodeCountAsync(FulfillmentJobDto job, TorrentItem torrent, CancellationToken ct)
    {
        if (torrent.Season is not int season) return null;
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

    private List<ImportedFileRecord> OrganizeTv(FulfillmentJobDto job, TorrentItem torrent, List<string> videoFiles, List<string> allFiles, EffectiveLibraryOrganization prefs, int? expectedEpisodeCount)
    {
        var records = new List<ImportedFileRecord>();

        if (!torrent.IsPack)
        {
            // Single-episode item — pick the largest video file (packs sometimes bundle a sample/extra
            // alongside the real episode even when it's not nominally a "pack").
            var best = videoFiles.OrderByDescending(f => SafeLength(f)).FirstOrDefault();
            if (best is null || torrent.Season is not int s || torrent.Episode is not int e) return records;

            var title = episodeTitles.GetEpisodeTitleAsync(job.TmdbId, s, e, CancellationToken.None).GetAwaiter().GetResult();
            var dest = naming.BuildEpisodePath(prefs, job, s, e, title, Path.GetExtension(best));
            TransferOne(best, dest, s, e, "video", records, prefs);
            PairSubtitle(best, dest, allFiles, prefs, s, e, records);
            return records;
        }

        if (torrent.Season is int season)
        {
            if (prefs.SplitSeasonPacks)
            {
                var mapped = splitter.Map(videoFiles, season, expectedEpisodeCount);
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

    private static bool PassesMinSize(string file, EffectiveLibraryOrganization prefs) =>
        SafeLength(file) >= prefs.MinVideoFileSizeMb * 1024 * 1024;

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; }
        catch { return 0; }
    }
}
