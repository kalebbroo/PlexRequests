using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Worker;

/// <summary>Last-line validation before an upgrade removes an old library file. The preview's immutable
/// target path is necessary but not sufficient: the file must still be under an approved managed root and,
/// for a storage optimization, still have the exact byte length reviewed by the administrator.</summary>
public static class ReplacementPathSafety
{
    public static bool CanDelete(string? path, FulfillmentJobDto job,
        EffectiveLibraryOrganization preferences, out string reason)
    {
        if (!TryFullPath(path, out var normalizedPath))
        {
            reason = "path is not absolute";
            return false;
        }

        var roots = preferences.Destinations.Where(destination => destination.Enabled)
            .Select(destination => destination.RootPath)
            .Concat(preferences.PlexPathMappings.Select(mapping => mapping.ManagedPathPrefix))
            .Append(job.LibraryDestination?.RootPath)
            .Where(root => !string.IsNullOrWhiteSpace(root));
        if (!roots.Any(root => PlexLibraryPathMapping.IsStrictlyUnder(normalizedPath, root)))
        {
            reason = "path is outside every approved managed library root";
            return false;
        }

        if (job.StorageOptimizationPolicy is { } policy)
        {
            var target = policy.Targets.FirstOrDefault(item => TryFullPath(item.DestinationPath,
                out var targetPath) && string.Equals(targetPath, normalizedPath, StringComparison.Ordinal));
            if (target is null)
            {
                reason = "path is not in the immutable optimization target list";
                return false;
            }
            if (target.CurrentSizeBytes <= 0)
            {
                reason = "reviewed file size is unknown";
                return false;
            }
            try
            {
                var info = new FileInfo(normalizedPath);
                if (!info.Exists)
                {
                    reason = "file no longer exists";
                    return false;
                }
                if (info.Length != target.CurrentSizeBytes)
                {
                    reason = $"file changed after preview (expected {target.CurrentSizeBytes} bytes, found {info.Length})";
                    return false;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                           or IOException or UnauthorizedAccessException)
            {
                reason = $"file could not be revalidated: {ex.Message}";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryFullPath(string? value, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value.Trim())) return false;
        try
        {
            path = Path.GetFullPath(value.Trim());
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
