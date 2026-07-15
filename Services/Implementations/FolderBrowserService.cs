using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// Lists one directory level at a time on the web app's own filesystem, for the Library Organization
/// admin page's folder picker. Read-only; no recursion (cheap, immune to deep-tree/symlink concerns).
/// Cross-platform: drive list as the root on Windows, "/" as the root on Linux/Mac.
/// </summary>
public interface IFolderBrowserService
{
    FolderBrowseResultDto Browse(string? path);
}

public class FolderBrowserService : IFolderBrowserService
{
    public FolderBrowseResultDto Browse(string? path)
    {
        var result = new FolderBrowseResultDto();

        if (string.IsNullOrWhiteSpace(path))
        {
            if (OperatingSystem.IsWindows())
            {
                result.CurrentPath = null;
                result.ParentPath = null;
                result.Directories = DriveInfo.GetDrives()
                    .Where(d => { try { return d.IsReady; } catch { return false; } })
                    .Select(d => new FolderEntryDto { Name = d.Name, FullPath = d.Name })
                    .ToList();
                return result;
            }
            path = "/"; // Linux/Mac: browse from the filesystem root
        }

        if (!Directory.Exists(path))
        {
            result.CurrentPath = path;
            return result; // empty Directories; UI shows "no such folder" rather than erroring
        }

        result.CurrentPath = path;
        // On Windows, Directory.GetParent("C:\") is null already; on Linux, Directory.GetParent("/") is
        // also null — both correctly signal "no Up from here".
        result.ParentPath = Directory.GetParent(path)?.FullName;

        try
        {
            result.Directories = Directory.GetDirectories(path)
                .Select(d => new FolderEntryDto { Name = Path.GetFileName(d), FullPath = d })
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // No permission to list this directory — return it as a valid (selectable) but empty
            // location rather than failing; the admin can still pick it, just can't browse deeper.
            result.Directories = new();
        }

        return result;
    }
}
