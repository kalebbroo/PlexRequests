using System.Text;
using System.Text.RegularExpressions;

namespace PlexRequests.Downloader.Organize;

/// <summary>Values a naming template can substitute. Any left null renders as an empty string.</summary>
public record TemplateContext(
    string? Title = null,
    string? ShowTitle = null,
    int? Year = null,
    int? Season = null,
    int? Episode = null,
    string? EpisodeTitle = null,
    string? Quality = null,
    string? Ext = null);

/// <summary>
/// Renders admin-configured naming templates (e.g. "{ShowTitle} ({Year})/Season {Season:00}/{ShowTitle}
/// - s{Season:00}e{Episode:00} - {EpisodeTitle}{Ext}") into a relative destination path, sanitizing every
/// substituted token value so a torrent-supplied or TMDB-supplied string can never inject an illegal
/// filesystem character or a path-traversal segment. Template literals (including "/" directory
/// separators) are never sanitized — only the values dropped into {tokens} are.
/// </summary>
public static class NamingTemplateEngine
{
    private static readonly Regex TokenRx = new(@"\{(\w+)(?::(0+))?\}", RegexOptions.Compiled);
    // Only bare zero-padding widths ("0"/"00") are supported in a format spec — anything else is ignored
    // (rendered with no padding) rather than passed through to a real format-string call, so a template
    // can never become an arbitrary format-string injection vector.
    private static readonly char[] InvalidPathChars = "<>:\"|?*\\".ToCharArray();

    public static string Render(string template, TemplateContext ctx)
    {
        if (string.IsNullOrWhiteSpace(template)) return string.Empty;

        var rendered = TokenRx.Replace(template, m =>
        {
            var token = m.Groups[1].Value;
            var pad = m.Groups[2].Success ? m.Groups[2].Value.Length : 0;
            string? raw = token switch
            {
                "Title" => ctx.Title,
                "ShowTitle" => ctx.ShowTitle ?? ctx.Title,
                "Year" => ctx.Year?.ToString(),
                "Season" => pad > 0 ? ctx.Season?.ToString($"D{Math.Min(pad, 2)}") : ctx.Season?.ToString(),
                "Episode" => pad > 0 ? ctx.Episode?.ToString($"D{Math.Min(pad, 2)}") : ctx.Episode?.ToString(),
                "EpisodeTitle" => ctx.EpisodeTitle,
                "Quality" => ctx.Quality,
                "Ext" => ctx.Ext, // our own extension whitelist, never user-controlled — no sanitization needed
                _ => null
            };
            return token == "Ext" ? (raw ?? string.Empty) : SanitizeComponent(raw);
        });

        // Collapse artifacts left by an empty token, e.g. "Show - s01e01 -  .mkv" when EpisodeTitle was
        // unknown, or a doubled separator when a whole segment resolved to nothing.
        rendered = Regex.Replace(rendered, @"\s+-\s+(?=[\\/]|\.\w+$|$)", "");
        rendered = Regex.Replace(rendered, @"[\\/]{2,}", Path.DirectorySeparatorChar.ToString());
        return rendered.Trim();
    }

    /// <summary>Strip characters illegal in a filesystem path segment, trim trailing dots/spaces (Windows-
    /// hostile, harmless elsewhere so kept for portability), collapse repeated whitespace, and truncate
    /// to a safe byte length. Never touches "/" or "\" — those are template literals, not token values.</summary>
    public static string SanitizeComponent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '/' or '\\') { sb.Append(' '); continue; } // a token value must never inject a path segment
            if (Array.IndexOf(InvalidPathChars, c) >= 0) continue;
            if (char.IsControl(c)) continue;
            sb.Append(c);
        }

        // TrimEnd('.', ' ') also doubles as traversal protection: a value consisting only of dots (e.g.
        // "..") is trimmed straight down to an empty string here, never surviving as a bare "." or ".."
        // segment — and an empty segment is safe (Path.Combine simply skips it).
        var cleaned = Regex.Replace(sb.ToString(), @"\s{2,}", " ").Trim().TrimEnd('.', ' ');

        const int maxBytes = 200;
        if (Encoding.UTF8.GetByteCount(cleaned) <= maxBytes) return cleaned;

        while (cleaned.Length > 0 && Encoding.UTF8.GetByteCount(cleaned) > maxBytes)
            cleaned = cleaned[..^1];
        return cleaned.TrimEnd('.', ' ');
    }
}
