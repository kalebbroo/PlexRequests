using PlexRequests.Downloader.Download;

namespace PlexRequests.Downloader.Worker;

/// <summary>
/// Works out where on disk a finished torrent actually landed.
///
/// Lifted verbatim out of FulfillmentPipeline so the reconciler can import too. It has to be ONE
/// implementation: the fallbacks encode hard-won knowledge about how Deluge reports paths (a single-file
/// torrent vs a folder, a save_path that is the parent vs the item itself, is_finished leading the flush
/// to disk), and a second copy would drift and start importing from the wrong directory — which moves the
/// wrong files into the library.
/// </summary>
public static class ImportSourceResolver
{
    public static string? Resolve(DownloadStatus status, int jobId, string torrentId, ILogger logger)
    {
        var saveDir = status.SavePath ?? string.Empty;

        if (status.Files.Count > 0)
        {
            string? viaFiles = status.Files.Count == 1
                ? Path.Combine(saveDir, NormalizeRelative(status.Files[0]))
                : CommonFolder(status.Files) is { } folder ? Path.Combine(saveDir, folder) : saveDir;

            if (Directory.Exists(viaFiles) || File.Exists(viaFiles))
            {
                logger.LogDebug("Job {JobId} torrent {TorrentId}: resolved import source via Deluge's file list -> {Path}", jobId, torrentId, viaFiles);
                return viaFiles;
            }
            logger.LogWarning("Job {JobId} torrent {TorrentId}: Deluge reported {Count} file(s) but the derived path doesn't exist ({Path}); falling back to name-based resolution",
                jobId, torrentId, status.Files.Count, viaFiles);
        }

        var viaName = Path.Combine(saveDir, status.Name);
        if (Directory.Exists(viaName) || File.Exists(viaName))
        {
            if (status.Files.Count == 0)
                logger.LogDebug("Job {JobId} torrent {TorrentId}: resolved import source via reported name (no file list available) -> {Path}", jobId, torrentId, viaName);
            return viaName;
        }

        logger.LogWarning("Job {JobId} torrent {TorrentId}: reported name \"{Name}\" doesn't exist under {SaveDir} either; falling back to picking the most-recently-modified entry in the save directory",
            jobId, torrentId, status.Name, saveDir);

        if (Directory.Exists(saveDir))
        {
            var newest = Directory.EnumerateFileSystemEntries(saveDir)
                .Select(p => new { Path = p, Modified = SafeLastWrite(p) })
                .OrderByDescending(x => x.Modified)
                .FirstOrDefault();
            if (newest is not null)
            {
                logger.LogWarning("Job {JobId} torrent {TorrentId}: heuristic fallback picked \"{Path}\" (most recently modified in {SaveDir}) — verify this is correct",
                    jobId, torrentId, newest.Path, saveDir);
                return newest.Path;
            }
        }

        return null;
    }

    private static string NormalizeRelative(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    // The shared top-level folder across a multi-file torrent's relative paths, if there is exactly one
    // (the normal case — a season pack's files all live under "Show.Name.S01/..."). Null when files sit
    // flat at the torrent root with no common containing folder.
    private static string? CommonFolder(IReadOnlyList<string> relativePaths)
    {
        string? common = null;
        foreach (var rel in relativePaths)
        {
            var normalized = NormalizeRelative(rel);
            var firstSegment = normalized.Split(Path.DirectorySeparatorChar, 2)[0];
            if (string.IsNullOrEmpty(firstSegment)) return null;
            if (common is null) common = firstSegment;
            else if (!string.Equals(common, firstSegment, StringComparison.Ordinal)) return null;
        }
        return common;
    }

    private static DateTime SafeLastWrite(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }
}
