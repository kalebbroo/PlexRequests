namespace PlexRequestsHosted.Shared.Navigation;

/// <summary>
/// Maps both current and legacy admin deep links onto the smaller task-oriented
/// workspace. Keeping this outside the Razor component makes bookmark compatibility
/// explicit and testable while the admin information architecture evolves.
/// </summary>
public static class AdminNavigationContext
{
    public const string DefaultDestination = "overview";

    public static AdminRouteSelection Resolve(string? tab, string? sub)
    {
        var normalizedTab = Normalize(tab);
        var normalizedSub = Normalize(sub);

        return normalizedTab switch
        {
            "requests" => Selection("requests", normalizedSub == "issues" ? "issues" : "queue"),
            "downloads" or "acquisition" => ResolveAcquisition(normalizedSub),
            "library" => ResolveLibrary(normalizedSub),
            "jobs" or "operations" => Selection("activity", "missing"),
            "users" or "access" => Selection("users"),
            "system" or "maintenance" => Selection("maintenance"),

            // Historical one-word routes are still accepted for old bookmarks and redirects.
            "approvals" => Selection("requests", "queue"),
            "issues" => Selection("requests", "issues"),
            "quality" => Selection("release-settings", "quality"),
            "assignment" => Selection("release-settings", "assignment"),
            "formats" => Selection("release-settings", "formats"),
            "indexers" or "sources" => Selection("sources"),
            "network" => Selection("libraries", "network"),
            "catalogs" => Selection("libraries", "catalogs"),
            "health" or "overview" or "platform" or "" => Selection(DefaultDestination),
            _ => Selection(DefaultDestination)
        };
    }

    private static AdminRouteSelection ResolveAcquisition(string sub) => sub switch
    {
        "quality" => Selection("release-settings", "quality"),
        "assignment" => Selection("release-settings", "assignment"),
        "formats" => Selection("release-settings", "formats"),
        "indexers" or "sources" => Selection("sources"),
        _ => Selection("release-settings", "basics")
    };

    private static AdminRouteSelection ResolveLibrary(string sub) => sub switch
    {
        "network" => Selection("libraries", "network"),
        "catalogs" or "media" => Selection("libraries", "catalogs"),
        _ => Selection("libraries", "destinations")
    };

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static AdminRouteSelection Selection(string destination, string section = "") =>
        new(destination, section);
}

public sealed record AdminRouteSelection(string Destination, string Section);
