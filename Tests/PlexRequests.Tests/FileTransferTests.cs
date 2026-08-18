using Microsoft.Extensions.Logging.Abstractions;
using PlexRequests.Downloader.Organize;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class FileTransferTests
{
    [Fact]
    public void Missing_replacement_source_never_deletes_existing_library_file()
    {
        var root = NewTempDirectory();
        try
        {
            var missingSource = Path.Combine(root, "already-moved.mkv");
            var destination = Path.Combine(root, "library", "episode.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, "known-good-720p");

            Assert.Throws<FileNotFoundException>(() => FileTransfer.Transfer(
                missingSource, destination, TransferMode.Copy, deleteSourceAfterImport: true, NullLogger.Instance));

            Assert.Equal("known-good-720p", File.ReadAllText(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Second_importer_cannot_erase_first_importers_committed_file()
    {
        var root = NewTempDirectory();
        try
        {
            var source = Path.Combine(root, "download.mkv");
            var destination = Path.Combine(root, "library", "episode.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(source, "verified-1080p");
            File.WriteAllText(destination, "old-720p");

            FileTransfer.Transfer(source, destination, TransferMode.Copy,
                deleteSourceAfterImport: true, NullLogger.Instance);
            Assert.False(File.Exists(source));

            Assert.Throws<FileNotFoundException>(() => FileTransfer.Transfer(
                source, destination, TransferMode.Copy, deleteSourceAfterImport: true, NullLogger.Instance));

            Assert.Equal("verified-1080p", File.ReadAllText(destination));
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, "*.partial"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plexrequests-transfer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
