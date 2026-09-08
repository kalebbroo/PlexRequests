using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Worker;

public interface IStorageArtifactCleaner
{
    StorageMaintenanceResult Sweep(IEnumerable<string> roots, IReadOnlySet<int> activeJobIds,
        TimeSpan minimumAge, bool remove);
}

/// <summary>Finds only names created by Plex Requests itself. It never treats arbitrary media, torrent
/// payloads, or another application's .partial files as removable.</summary>
public sealed class StorageArtifactCleaner(ILogger<StorageArtifactCleaner> logger) : IStorageArtifactCleaner
{
    private const int MaxEntriesPerRoot = 200_000;

    public StorageMaintenanceResult Sweep(IEnumerable<string> roots, IReadOnlySet<int> activeJobIds,
        TimeSpan minimumAge, bool remove)
    {
        var cutoff = DateTime.UtcNow - minimumAge;
        var candidates = new List<StorageCleanupCandidateDto>();
        var removedCount = 0;
        long removedBytes = 0;

        foreach (var root in NormalizeRoots(roots))
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                ScanStagingRoot(Path.Combine(root, ".plexrequests-staging"));
                ScanPartials(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Storage artifact scan could not read {Root}", root);
            }
        }

        return new StorageMaintenanceResult(candidates, removedCount, removedBytes);

        void ScanStagingRoot(string stagingRoot)
        {
            if (!Directory.Exists(stagingRoot)) return;
            foreach (var directory in Directory.EnumerateDirectories(stagingRoot))
            {
                if (IsReparsePoint(directory)) continue;
                var name = Path.GetFileName(directory);
                var separator = name.IndexOf('-');
                var active = separator > 0 && int.TryParse(name[..separator], out var jobId)
                    && activeJobIds.Contains(jobId);
                var (size, modified) = DirectoryMetrics(directory);
                if (active || modified > cutoff) continue;
                var candidate = Candidate("Staging folder", directory, size, modified,
                    "Archive/import staging left by a job that is no longer active");
                if (remove && TryDeleteDirectory(directory))
                {
                    removedCount++;
                    removedBytes += size;
                }
                else candidates.Add(candidate);
            }
            TryDeleteEmpty(stagingRoot);
        }

        void ScanPartials(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            var inspected = 0;
            while (stack.Count > 0 && inspected < MaxEntriesPerRoot)
            {
                var current = stack.Pop();
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(current); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Skipping unreadable storage folder {Path}", current);
                    continue;
                }
                foreach (var entry in entries)
                {
                    if (++inspected > MaxEntriesPerRoot) break;
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(entry); }
                    catch { continue; }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if ((attributes & FileAttributes.ReparsePoint) != 0
                            || Path.GetFileName(entry).Equals(".plexrequests-staging", StringComparison.Ordinal))
                            continue;
                        stack.Push(entry);
                        continue;
                    }
                    var name = Path.GetFileName(entry);
                    var ownedPartial = name.StartsWith(".", StringComparison.Ordinal)
                        && name.Contains(".plexrequests-", StringComparison.Ordinal)
                        && name.EndsWith(".partial", StringComparison.Ordinal);
                    var ownedRemux = name.StartsWith(".", StringComparison.Ordinal)
                        && name.Contains(".plexrequests-remux-", StringComparison.Ordinal)
                        && name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);
                    if (!ownedPartial && !ownedRemux)
                        continue;
                    DateTime modified;
                    long size;
                    try
                    {
                        var info = new FileInfo(entry);
                        modified = info.LastWriteTimeUtc;
                        size = info.Length;
                    }
                    catch { continue; }
                    if (modified > cutoff) continue;
                    var candidate = Candidate("Partial file", entry, size, modified,
                        "Atomic import file left incomplete after its job stopped");
                    if (remove && TryDeleteFile(entry))
                    {
                        removedCount++;
                        removedBytes += size;
                    }
                    else candidates.Add(candidate);
                }
            }
            if (inspected >= MaxEntriesPerRoot)
                logger.LogWarning("Storage artifact scan reached its {Limit}-entry safety bound under {Root}",
                    MaxEntriesPerRoot, root);
        }

        bool TryDeleteDirectory(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                logger.LogInformation("Removed stale Plex Requests staging folder {Path}", path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove stale staging folder {Path}", path);
                return false;
            }
        }

        bool TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
                logger.LogInformation("Removed stale Plex Requests partial file {Path}", path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove stale partial file {Path}", path);
                return false;
            }
        }
    }

    private static StorageCleanupCandidateDto Candidate(string kind, string path, long size,
        DateTime modified, string reason) => new()
        {
            Kind = kind,
            Path = path,
            SizeBytes = size,
            LastModifiedAt = modified,
            Reason = reason
        };

    private static (long Size, DateTime Modified) DirectoryMetrics(string path)
    {
        long size = 0;
        var latest = DateTime.MinValue;
        try
        {
            var stack = new Stack<string>();
            stack.Push(path);
            var inspected = 0;
            while (stack.Count > 0 && inspected < MaxEntriesPerRoot)
            {
                var current = stack.Pop();
                latest = Later(latest, Directory.GetLastWriteTimeUtc(current));
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    if (++inspected > MaxEntriesPerRoot) break;
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        stack.Push(entry);
                        continue;
                    }
                    var info = new FileInfo(entry);
                    size += info.Length;
                    latest = Later(latest, info.LastWriteTimeUtc);
                }
            }
        }
        catch { }
        if (latest == DateTime.MinValue)
        {
            try { latest = Directory.GetLastWriteTimeUtc(path); }
            catch { latest = DateTime.UtcNow; }
        }
        return (size, latest);
    }

    private static void TryDeleteEmpty(string path)
    {
        try { if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
        catch { }
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static IReadOnlyList<string> NormalizeRoots(IEnumerable<string> roots)
    {
        var candidates = new List<string>();
        foreach (var value in roots.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            try { candidates.Add(Path.GetFullPath(value)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var kept = new List<string>();
        foreach (var candidate in candidates.Distinct(PathComparer()).OrderBy(x => x.Length))
        {
            if (kept.Any(root => IsWithin(candidate, root, comparison))) continue;
            kept.Add(candidate);
        }
        return kept;
    }

    private static bool IsWithin(string path, string root, StringComparison comparison)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, comparison);
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }

    private static DateTime Later(DateTime first, DateTime second) => first >= second ? first : second;
}
