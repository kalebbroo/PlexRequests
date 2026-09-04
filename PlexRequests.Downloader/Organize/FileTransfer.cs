using System.Runtime.InteropServices;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Organize;

/// <summary>Places one file at its destination per the configured <see cref="TransferMode"/>.</summary>
public static partial class FileTransfer
{
    private static volatile bool _warnedNonLinuxHardlink;

    public static void Transfer(string source, string dest, TransferMode mode, bool deleteSourceAfterImport,
        ILogger logger, Action<string>? prepareStaged = null)
    {
        var sourceFullPath = Path.GetFullPath(source);
        var destinationFullPath = Path.GetFullPath(dest);
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(sourceFullPath, destinationFullPath, pathComparison)) return;

        // Read the source before touching the existing library file. Besides giving us the expected length
        // for verification, this is the safety boundary that the old implementation lacked: it deleted dest
        // first, then discovered that another importer had already moved source, leaving neither copy behind.
        var expectedLength = new FileInfo(sourceFullPath).Length;
        var destDir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
        var stagingPath = Path.Combine(destDir ?? Directory.GetCurrentDirectory(),
            $".{Path.GetFileName(dest)}.plexrequests-{Guid.NewGuid():N}.partial");

        try
        {
            var staged = false;
            if (mode == TransferMode.Hardlink && prepareStaged is null)
            {
                if (OperatingSystem.IsLinux() && link(sourceFullPath, stagingPath) == 0)
                {
                    staged = true;
                }
                else if (!OperatingSystem.IsLinux())
                {
                    if (!_warnedNonLinuxHardlink)
                    {
                        _warnedNonLinuxHardlink = true;
                        logger.LogWarning("TransferMode=Hardlink configured but the host OS is not Linux; every import will fall back to Copy (uses more disk; the torrent still keeps seeding from its own directory).");
                    }
                }
                else
                {
                    logger.LogInformation("Hardlink unavailable for {Dest} (likely cross-device); copying instead", dest);
                }
            }

            // Move also stages by copying. That costs no extra I/O for the normal cross-filesystem
            // download->NAS move, and it preserves source until the replacement has been committed. A direct
            // File.Move cannot provide that guarantee when an old destination already exists.
            if (!staged) File.Copy(sourceFullPath, stagingPath, overwrite: false);

            var stagedLength = new FileInfo(stagingPath).Length;
            if (stagedLength != expectedLength)
                throw new IOException($"Staged file size mismatch for '{dest}': expected {expectedLength} bytes, copied {stagedLength} bytes");

            // Container metadata edits are made only on this private staged copy. When Hardlink is
            // configured, the presence of this callback deliberately forced the copy path above so the
            // torrent payload remains byte-for-byte intact for seeding and client verification.
            prepareStaged?.Invoke(stagingPath);
            var preparedLength = new FileInfo(stagingPath).Length;
            if (preparedLength <= 0)
                throw new IOException($"Staged file became empty while preparing '{dest}'");

            // stagingPath is in the destination directory, so this is a same-filesystem atomic rename. The
            // previous library copy remains intact until this exact operation succeeds.
            File.Move(stagingPath, destinationFullPath, overwrite: true);

            var committedLength = new FileInfo(destinationFullPath).Length;
            if (committedLength != preparedLength)
                throw new IOException($"Committed file size mismatch for '{dest}': expected {preparedLength} bytes, found {committedLength} bytes");

            if (mode == TransferMode.Move || (mode == TransferMode.Copy && deleteSourceAfterImport))
                TryDelete(sourceFullPath, logger);
        }
        finally
        {
            // A crash can leave a partial file, but it is deliberately hidden and never replaces the Plex
            // path. Normal failures clean it immediately.
            if (File.Exists(stagingPath)) TryDelete(stagingPath, logger);
        }
    }

    private static void TryDelete(string path, ILogger logger)
    {
        try { File.Delete(path); }
        catch (Exception ex) { logger.LogDebug(ex, "Could not delete source file {Path} after import", path); }
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int link(string oldpath, string newpath);
}
