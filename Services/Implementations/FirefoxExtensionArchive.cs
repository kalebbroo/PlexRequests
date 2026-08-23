using System.IO.Compression;

namespace PlexRequestsHosted.Services.Implementations;

public static class FirefoxExtensionArchive
{
    private static readonly string[] Files =
    [
        "manifest.json",
        "background.js",
        "content.js",
        "hydration.js",
        "telemetry.js",
        "parser.js",
        "sources.js",
        "popup/popup.html",
        "popup/popup.css",
        "popup/popup.js",
        "icons/capture.svg",
        "README.md"
    ];

    public static byte[] Create(string contentRootPath)
    {
        var extensionRoot = Path.GetFullPath(Path.Combine(contentRootPath, "BrowserExtension", "firefox"));
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var relativePath in Files)
            {
                var fullPath = Path.GetFullPath(Path.Combine(extensionRoot, relativePath));
                if (!fullPath.StartsWith(extensionRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || !File.Exists(fullPath))
                    throw new FileNotFoundException($"Firefox extension asset is missing: {relativePath}", fullPath);
                archive.CreateEntryFromFile(fullPath, relativePath, CompressionLevel.Optimal);
            }
        }
        return output.ToArray();
    }
}
