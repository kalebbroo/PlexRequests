using System.Runtime.InteropServices;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Organize;

/// <summary>Places one file at its destination per the configured <see cref="TransferMode"/>.</summary>
public static partial class FileTransfer
{
    private static volatile bool _warnedNonLinuxHardlink;

    public static void Transfer(string source, string dest, TransferMode mode, bool deleteSourceAfterImport, ILogger logger)
    {
        var destDir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
        if (File.Exists(dest)) File.Delete(dest); // no dedup/versioning — a re-import intentionally replaces

        switch (mode)
        {
            case TransferMode.Hardlink:
                if (OperatingSystem.IsLinux() && link(source, dest) == 0) return;
                if (!OperatingSystem.IsLinux())
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
                File.Copy(source, dest, overwrite: true);
                return;

            case TransferMode.Copy:
                File.Copy(source, dest, overwrite: true);
                if (deleteSourceAfterImport) TryDelete(source, logger);
                return;

            case TransferMode.Move:
                try { File.Move(source, dest, overwrite: true); }
                catch (IOException)
                {
                    // Cross-filesystem move isn't atomic in .NET — fall back to copy+delete.
                    File.Copy(source, dest, overwrite: true);
                    TryDelete(source, logger);
                }
                return;
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
