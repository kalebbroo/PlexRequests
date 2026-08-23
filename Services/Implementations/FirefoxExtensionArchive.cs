using System.IO.Compression;
using System.Text.Json;

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

    public static FirefoxExtensionInfo Inspect(string contentRootPath)
    {
        var manifestPath = Path.Combine(ExtensionRoot(contentRootPath), "manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Firefox extension manifest is missing.", manifestPath);
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var version = document.RootElement.GetProperty("version").GetString();
        if (string.IsNullOrWhiteSpace(version) || !Version.TryParse(version, out _))
            throw new InvalidDataException("Firefox extension manifest has an invalid version.");
        return new FirefoxExtensionInfo(version);
    }

    public static byte[] Create(string contentRootPath)
    {
        var extensionRoot = ExtensionRoot(contentRootPath);
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

    private static string ExtensionRoot(string contentRootPath) =>
        Path.GetFullPath(Path.Combine(contentRootPath, "BrowserExtension", "firefox"));
}

public sealed record FirefoxExtensionInfo(string CurrentVersion)
{
    public bool IsUpdateAvailable(string? installedVersion)
    {
        if (!TryNormalize(CurrentVersion, out var current)) return false;
        return !TryNormalize(installedVersion, out var installed) || installed < current;
    }

    private static bool TryNormalize(string? value, out Version version)
    {
        version = new Version();
        if (!Version.TryParse(value, out var parsed)) return false;
        version = new Version(
            parsed.Major,
            parsed.Minor,
            Math.Max(0, parsed.Build),
            Math.Max(0, parsed.Revision));
        return true;
    }
}
