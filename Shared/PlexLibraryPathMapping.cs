using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Shared;

/// <summary>Fail-closed filesystem translation for media paths returned by Plex. A mapping is selected by
/// section and directory-boundary prefix; the longest source prefix wins. The translated result is always
/// rechecked beneath the configured managed root.</summary>
public static class PlexLibraryPathMapping
{
    public static bool TryNormalize(PlexPathMappingDto value, out PlexPathMappingDto normalized,
        out string? error)
    {
        normalized = new PlexPathMappingDto();
        var section = value.PlexSectionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(section))
        {
            error = "Every Plex path mapping needs a Plex library.";
            return false;
        }
        if (!TryNormalizeRoot(value.PlexPathPrefix, out var plexRoot))
        {
            error = $"Plex path mapping for section {section} needs an absolute, non-root Plex folder.";
            return false;
        }
        if (!TryNormalizeRoot(value.ManagedPathPrefix, out var managedRoot))
        {
            error = $"Plex path mapping for section {section} needs an absolute, non-root Plex Requests folder.";
            return false;
        }

        normalized = new PlexPathMappingDto
        {
            PlexSectionId = section,
            PlexPathPrefix = plexRoot,
            ManagedPathPrefix = managedRoot
        };
        error = null;
        return true;
    }

    public static bool TryMap(string? sectionId, string? plexPath,
        IEnumerable<PlexPathMappingDto>? mappings, out string managedPath)
    {
        managedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(sectionId) || !TryNormalizeFile(plexPath, out var source)) return false;

        foreach (var mapping in (mappings ?? []).Select(TryGetNormalized)
                     .Where(item => item is not null
                                    && string.Equals(item.PlexSectionId, sectionId.Trim(),
                                        StringComparison.OrdinalIgnoreCase)
                                    && IsStrictlyUnder(source, item.PlexPathPrefix))
                     .OrderByDescending(item => item!.PlexPathPrefix.Length))
        {
            var relative = source[(mapping!.PlexPathPrefix.Length + 1)..];
            var candidate = Path.GetFullPath(Path.Combine(mapping.ManagedPathPrefix, relative));
            if (!IsStrictlyUnder(candidate, mapping.ManagedPathPrefix)) continue;
            managedPath = candidate;
            return true;
        }
        return false;
    }

    public static bool IsStrictlyUnder(string? path, string? root)
    {
        if (!TryNormalizeFile(path, out var normalizedPath) || !TryNormalizeRoot(root, out var normalizedRoot))
            return false;
        return normalizedPath.Length > normalizedRoot.Length
               && normalizedPath.StartsWith(normalizedRoot, StringComparison.Ordinal)
               && normalizedPath[normalizedRoot.Length] == Path.DirectorySeparatorChar;
    }

    private static PlexPathMappingDto? TryGetNormalized(PlexPathMappingDto value) =>
        TryNormalize(value, out var normalized, out _) ? normalized : null;

    private static bool TryNormalizeRoot(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!TryNormalizeFile(value, out var full)) return false;
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root)) return false;
        full = Path.TrimEndingDirectorySeparator(full);
        if (string.Equals(full, Path.TrimEndingDirectorySeparator(root), StringComparison.Ordinal)) return false;
        normalized = full;
        return true;
    }

    private static bool TryNormalizeFile(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value.Trim())) return false;
        try
        {
            normalized = Path.GetFullPath(value.Trim());
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
