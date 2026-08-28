using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared.Navigation;

/// <summary>Builds and validates the local return path carried through nested music pages.</summary>
public static class MusicNavigationContext
{
    public const string DefaultUrl = "/music";

    public static string ValidateReturnUrl(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 2048
            || candidate[0] != '/' || candidate.StartsWith("//", StringComparison.Ordinal)
            || candidate.Contains('\\') || candidate.Any(char.IsControl)
            || !Uri.TryCreate(candidate, UriKind.Relative, out _))
            return DefaultUrl;

        var separator = candidate.IndexOfAny(['?', '#']);
        var path = separator >= 0 ? candidate[..separator] : candidate;
        return path.Equals("/music", StringComparison.Ordinal)
               || path.StartsWith("/music/", StringComparison.Ordinal)
               || path.StartsWith("/media/music/", StringComparison.Ordinal)
            ? candidate
            : DefaultUrl;
    }

    public static string ValidateLabel(string? candidate, string fallback = "Music")
    {
        if (string.IsNullOrWhiteSpace(candidate)) return fallback;
        var value = new string(candidate.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (value.Length == 0) return fallback;
        return value.Length <= 80 ? value : value[..80];
    }

    public static string ResolveReturnLabel(string? returnUrl, string? candidate)
        => !string.IsNullOrWhiteSpace(returnUrl)
           && string.Equals(ValidateReturnUrl(returnUrl), returnUrl, StringComparison.Ordinal)
            ? ValidateLabel(candidate)
            : "Music";

    public static string AddReturnContext(string href, string? returnUrl, string? returnLabel)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return href;
        var safeUrl = ValidateReturnUrl(returnUrl);
        var safeLabel = ValidateLabel(returnLabel);
        var separator = href.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{href}{separator}returnUrl={Uri.EscapeDataString(safeUrl)}&returnLabel={Uri.EscapeDataString(safeLabel)}";
    }

    public static string BuildSearchUrl(string? query, MediaKind? kind = null)
    {
        var parameters = new List<string>();
        if (!string.IsNullOrWhiteSpace(query))
            parameters.Add($"q={Uri.EscapeDataString(query.Trim())}");
        if (kind is MediaKind.Artist or MediaKind.Album or MediaKind.Track)
            parameters.Add($"kind={kind.Value.ToString().ToLowerInvariant()}");
        return parameters.Count == 0 ? DefaultUrl : $"{DefaultUrl}?{string.Join('&', parameters)}";
    }
}
