using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Worker;

internal sealed class RetiredLibraryPathException(string message) : InvalidOperationException(message);

internal sealed record LibraryAuditPathContext(
    string DestinationPath,
    string? LibraryDestinationRootPath,
    MediaType MediaType,
    Quality Quality,
    IReadOnlyList<string> Genres,
    bool IsAnime);

/// <summary>Resolves an audit path only inside a currently configured library root. Both read-only scans
/// and legacy repairs use this boundary so a stale or malicious database path cannot reach downloads,
/// container storage, or another filesystem.</summary>
internal static class LibraryAuditPathResolver
{
    internal static (string Path, string Root) Resolve(LibraryAuditPathContext task,
        EffectiveLibraryOrganization preferences, string? requiredExtension = null)
    {
        var destinationPath = task.DestinationPath;
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new InvalidOperationException("The audited library path is empty");
        if (requiredExtension is not null
            && !Path.GetExtension(destinationPath).Equals(requiredExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Only audited {requiredExtension} library files can be prepared");

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var roots = preferences.Destinations
            .Where(destination => destination.Enabled)
            .Select(destination => destination.RootPath)
            .Concat([preferences.MoviePath, preferences.TvPath, preferences.MusicPath])
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(comparison == StringComparison.OrdinalIgnoreCase
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .OrderByDescending(root => root.Length)
            .ToList();
        var fullPath = Path.IsPathFullyQualified(destinationPath)
            ? Path.GetFullPath(destinationPath)
            : ResolveRelativePath(task, preferences, roots, comparison);
        foreach (var root in roots)
        {
            if (IsWithin(fullPath, root, comparison)) return (fullPath, root);
        }

        throw new RetiredLibraryPathException(PlaybackPreparationReportDto.LegacyOutsideRootFailure);
    }

    private static string ResolveRelativePath(LibraryAuditPathContext task,
        EffectiveLibraryOrganization preferences, IEnumerable<string> configuredRoots,
        StringComparison comparison)
    {
        var selectedRoot = task.LibraryDestinationRootPath;
        if (string.IsNullOrWhiteSpace(selectedRoot))
            selectedRoot = preferences.Resolve(task.MediaType, task.Quality, task.Genres,
                task.IsAnime, isEpisode: true).Root;
        if (string.IsNullOrWhiteSpace(selectedRoot))
            throw new InvalidOperationException("The legacy relative audit path has no configured library destination");

        selectedRoot = Path.GetFullPath(selectedRoot);
        if (!configuredRoots.Any(root => string.Equals(Path.TrimEndingDirectorySeparator(root),
                Path.TrimEndingDirectorySeparator(selectedRoot), comparison)))
            throw new RetiredLibraryPathException("The legacy audit destination is no longer configured");

        var fullPath = Path.GetFullPath(Path.Combine(selectedRoot, task.DestinationPath));
        if (!IsWithin(fullPath, selectedRoot, comparison))
            throw new InvalidOperationException("The legacy relative audit path escapes its library root");
        return fullPath;
    }

    private static bool IsWithin(string path, string root, StringComparison comparison)
    {
        var normalized = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalized, comparison);
    }
}
