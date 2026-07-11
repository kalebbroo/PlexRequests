using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Import;

public interface ILibraryImporter
{
    /// <summary>Place the completed download into the Plex library path for this job's media type.</summary>
    Task<bool> ImportAsync(FulfillmentJobDto job, string sourcePath, CancellationToken ct);
}

/// <summary>
/// Hardlinks (default) or moves the completed download into the movie/TV library path so Plex can
/// index it. Hardlinking keeps the torrent seeding while exposing the file to Plex, but requires the
/// download and library paths to share a filesystem; on a cross-device error it falls back to copy.
/// </summary>
public partial class LibraryImporter(IOptions<LibraryOptions> options, ILogger<LibraryImporter> logger) : ILibraryImporter
{
    private readonly LibraryOptions _opts = options.Value;
    private readonly ILogger<LibraryImporter> _logger = logger;

    private static readonly string[] VideoExtensions = { ".mkv", ".mp4", ".avi", ".m4v", ".ts", ".mov", ".wmv" };

    public Task<bool> ImportAsync(FulfillmentJobDto job, string sourcePath, CancellationToken ct)
        => Task.Run(() => Import(job, sourcePath), ct);

    private bool Import(FulfillmentJobDto job, string sourcePath)
    {
        var targetRoot = job.MediaType == MediaType.Movie ? _opts.MoviePath : _opts.TvPath;
        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            _logger.LogError("No library path configured for {Type}; cannot import \"{Title}\"", job.MediaType, job.Title);
            return false;
        }
        Directory.CreateDirectory(targetRoot);

        try
        {
            if (Directory.Exists(sourcePath))
            {
                var dest = Path.Combine(targetRoot, Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar)));
                TransferDirectory(sourcePath, dest);
            }
            else if (File.Exists(sourcePath))
            {
                var dest = Path.Combine(targetRoot, Path.GetFileName(sourcePath));
                TransferFile(sourcePath, dest);
            }
            else
            {
                _logger.LogError("Source path does not exist: {Path}", sourcePath);
                return false;
            }
            _logger.LogInformation("Imported \"{Title}\" into {Target}", job.Title, targetRoot);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import failed for \"{Title}\" from {Source}", job.Title, sourcePath);
            return false;
        }
    }

    private void TransferDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            // Only bring across video files (skip samples/NFOs/etc.), but keep relative layout.
            if (!VideoExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
            if (Path.GetFileName(file).Contains("sample", StringComparison.OrdinalIgnoreCase)) continue;

            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            TransferFile(file, target);
        }
    }

    private void TransferFile(string source, string dest)
    {
        if (File.Exists(dest)) File.Delete(dest);

        if (_opts.Hardlink)
        {
            if (OperatingSystem.IsLinux() && link(source, dest) == 0) return;
            // Cross-device or non-Linux: fall back to copy so we still keep the source for seeding.
            _logger.LogDebug("Hardlink unavailable for {Dest}; copying instead", dest);
            File.Copy(source, dest, overwrite: true);
        }
        else
        {
            File.Move(source, dest, overwrite: true);
        }
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int link(string oldpath, string newpath);
}
