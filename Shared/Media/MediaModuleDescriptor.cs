using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared.Media;

[Flags]
public enum MediaModuleCapabilities
{
    None = 0,
    Search = 1 << 0,
    Discovery = 1 << 1,
    Request = 1 << 2,
    AutomaticFulfillment = 1 << 3,
    InteractiveSearch = 1 << 4,
    LibraryImport = 1 << 5,
    PlexVerification = 1 << 6,
    Monitoring = 1 << 7
}

/// <summary>Data-only description consumed by navigation, search, APIs and the admin UI.</summary>
public sealed record MediaModuleDescriptor
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required MediaType MediaType { get; init; }
    public required IReadOnlyList<MediaKind> Kinds { get; init; }
    public required IReadOnlyList<RequestScopeKind> RequestScopes { get; init; }
    public required MediaModuleCapabilities Capabilities { get; init; }
    public bool Enabled { get; init; }
    public string? UnavailableReason { get; init; }
}
