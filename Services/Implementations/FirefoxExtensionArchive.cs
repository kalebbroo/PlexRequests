using System.IO.Compression;
using System.Text.Json;

namespace PlexRequestsHosted.Services.Implementations;

public static class FirefoxExtensionArchive
{
    public static FirefoxExtensionInfo Inspect(string contentRootPath)
    {
        using var document = ReadManifest(ExtensionRoot(contentRootPath));
        var version = document.RootElement.GetProperty("version").GetString();
        if (string.IsNullOrWhiteSpace(version) || !Version.TryParse(version, out _))
            throw new InvalidDataException("Firefox extension manifest has an invalid version.");
        return new FirefoxExtensionInfo(version);
    }

    public static byte[] Create(string contentRootPath)
    {
        var extensionRoot = ExtensionRoot(contentRootPath);
        using var manifest = ReadManifest(extensionRoot);
        ValidateManifestAssets(extensionRoot, manifest.RootElement);
        var files = Directory.EnumerateFiles(extensionRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(extensionRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var relativePath in files)
            {
                var fullPath = ResolveAsset(extensionRoot, relativePath);
                archive.CreateEntryFromFile(fullPath, relativePath, CompressionLevel.Optimal);
            }
        }
        return output.ToArray();
    }

    private static JsonDocument ReadManifest(string extensionRoot)
    {
        var manifestPath = Path.Combine(extensionRoot, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Firefox extension manifest is missing.", manifestPath);
        return JsonDocument.Parse(File.ReadAllBytes(manifestPath));
    }

    private static void ValidateManifestAssets(string extensionRoot, JsonElement manifest)
    {
        var assets = new HashSet<string>(StringComparer.Ordinal) { "manifest.json" };
        AddStringArray(manifest, "background", "scripts", assets);
        if (manifest.TryGetProperty("content_scripts", out var contentScripts))
        {
            foreach (var contentScript in contentScripts.EnumerateArray())
            {
                AddStringArray(contentScript, "js", assets);
                AddStringArray(contentScript, "css", assets);
            }
        }
        AddString(manifest, "action", "default_popup", assets);
        AddIconPaths(manifest, "action", "default_icon", assets);
        AddIconPaths(manifest, "icons", assets);

        foreach (var asset in assets)
            _ = ResolveAsset(extensionRoot, asset);
    }

    private static void AddStringArray(JsonElement parent, string objectName, string propertyName, ISet<string> assets)
    {
        if (parent.TryGetProperty(objectName, out var child)) AddStringArray(child, propertyName, assets);
    }

    private static void AddStringArray(JsonElement parent, string propertyName, ISet<string> assets)
    {
        if (!parent.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array) return;
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } path)
                assets.Add(path);
        }
    }

    private static void AddString(JsonElement parent, string objectName, string propertyName, ISet<string> assets)
    {
        if (parent.TryGetProperty(objectName, out var child)
            && child.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } path)
            assets.Add(path);
    }

    private static void AddIconPaths(JsonElement parent, string propertyName, ISet<string> assets)
    {
        if (parent.TryGetProperty(propertyName, out var icons)) AddIconPaths(icons, assets);
    }

    private static void AddIconPaths(JsonElement parent, string objectName, string propertyName, ISet<string> assets)
    {
        if (parent.TryGetProperty(objectName, out var child)
            && child.TryGetProperty(propertyName, out var icons))
            AddIconPaths(icons, assets);
    }

    private static void AddIconPaths(JsonElement icons, ISet<string> assets)
    {
        if (icons.ValueKind == JsonValueKind.String && icons.GetString() is { Length: > 0 } path)
        {
            assets.Add(path);
            return;
        }
        if (icons.ValueKind != JsonValueKind.Object) return;
        foreach (var icon in icons.EnumerateObject())
        {
            if (icon.Value.ValueKind == JsonValueKind.String && icon.Value.GetString() is { Length: > 0 } iconPath)
                assets.Add(iconPath);
        }
    }

    private static string ResolveAsset(string extensionRoot, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(extensionRoot, normalized));
        if (!fullPath.StartsWith(extensionRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"Firefox extension asset path is invalid: {relativePath}");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Firefox extension asset is missing: {relativePath}", fullPath);
        return fullPath;
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
