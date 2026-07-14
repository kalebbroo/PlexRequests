using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace PlexRequests.Downloader.Organize;

public interface IArchiveExtractor
{
    /// <summary>True if this filename is a RAR/ZIP/7z archive at all (any volume).</summary>
    bool LooksLikeArchive(string filePath);

    /// <summary>
    /// True if this file is a CONTINUATION volume of a multi-volume archive (e.g. "*.r01", "*.part2.rar")
    /// rather than the first-volume entry point that should actually be opened — SharpCompress follows
    /// the volume chain itself once given the first volume, so continuation files must be skipped as
    /// extraction entry points (they'd otherwise each be "opened" redundantly / fail on their own).
    /// </summary>
    bool IsContinuationVolume(string filePath);

    /// <summary>
    /// Extract every file entry from the archive (auto-following sibling volumes for a multi-part RAR)
    /// into <paramref name="destinationDirectory"/>. Throws on a password-protected or corrupt archive —
    /// callers should treat that as an ordinary import failure, not a crash.
    /// </summary>
    Task ExtractAsync(string archiveFilePath, string destinationDirectory, CancellationToken ct);
}

/// <summary>
/// Wraps SharpCompress (MIT-licensed, pure-.NET RAR1-5/ZIP/7z reader — no external unrar binary needed)
/// to make scene-style RAR-packed releases visible to the organizer. Without this, a season pack whose
/// video files are wrapped in .rar/.r00 parts is invisible to a plain file-extension filter and silently
/// imports zero files.
/// </summary>
public partial class ArchiveExtractor(ILogger<ArchiveExtractor> logger) : IArchiveExtractor
{
    private static readonly string[] ArchiveExtensions = { ".rar", ".zip", ".7z" };

    public bool LooksLikeArchive(string filePath) =>
        ArchiveExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase) ||
        OldStyleRarPartRegex().IsMatch(Path.GetFileName(filePath));

    public bool IsContinuationVolume(string filePath)
    {
        var name = Path.GetFileName(filePath);
        if (OldStyleRarPartRegex().IsMatch(name)) return true; // .r00, .r01, ... (the .rar itself is the first volume)

        var partMatch = NewStylePartRegex().Match(name);
        if (partMatch.Success)
        {
            var n = int.Parse(partMatch.Groups[1].Value);
            return n != 1; // part1.rar/part01.rar is the entry point; part2+ are continuations
        }

        return false;
    }

    public Task ExtractAsync(string archiveFilePath, string destinationDirectory, CancellationToken ct) =>
        Task.Run(() =>
        {
            Directory.CreateDirectory(destinationDirectory);
            var destRoot = Path.GetFullPath(destinationDirectory);
            var destRootWithSeparator = destRoot.EndsWith(Path.DirectorySeparatorChar) ? destRoot : destRoot + Path.DirectorySeparatorChar;

            using var archive = ArchiveFactory.OpenArchive(archiveFilePath, new ReaderOptions());
            var extracted = 0;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.IsDirectory) continue;

                // Extract each entry to a manually-computed, validated path rather than trusting any
                // library-provided "write to directory" combine — an archive's internal entry names are
                // fully attacker/uploader-controlled, so this is the actual defense against a zip-slip
                // path-traversal entry (e.g. "../../../etc/passwd") escaping the staging directory.
                var safeRelative = SanitizeEntryPath(entry.Key);
                if (safeRelative is null)
                {
                    logger.LogWarning("Skipped archive entry with unsafe path: {Entry}", entry.Key);
                    continue;
                }

                var targetPath = Path.GetFullPath(Path.Combine(destRoot, safeRelative));
                if (!targetPath.StartsWith(destRootWithSeparator, StringComparison.Ordinal))
                {
                    logger.LogWarning("Skipped archive entry escaping the destination directory: {Entry}", entry.Key);
                    continue;
                }

                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
                entry.WriteToFile(targetPath, new ExtractionOptions { Overwrite = true });
                extracted++;
            }
            logger.LogInformation("Extracted archive {Path} -> {Dest} ({Count} file(s))", archiveFilePath, destinationDirectory, extracted);
        }, ct);

    // Reject/neutralize traversal ("..") and rooted segments in an archive entry's internal path before
    // it's ever combined with the destination directory. Returns null when nothing safe remains.
    private static string? SanitizeEntryPath(string? entryKey)
    {
        if (string.IsNullOrWhiteSpace(entryKey)) return null;
        var segments = entryKey.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != "." && s != "..")
            .Select(NamingTemplateEngine.SanitizeComponent)
            .Where(s => s.Length > 0)
            .ToArray();
        return segments.Length == 0 ? null : Path.Combine(segments);
    }

    [GeneratedRegex(@"\.r\d{2,3}$", RegexOptions.IgnoreCase)]
    private static partial Regex OldStyleRarPartRegex();

    [GeneratedRegex(@"\.part0*(\d+)\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex NewStylePartRegex();
}
