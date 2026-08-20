using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared.Releases;

/// <summary>
/// Protocol-neutral instructions for acquiring one release. Locator is opaque outside the backend that
/// owns <see cref="Protocol"/>; SourceId is the stable provider identity used for deduplication and
/// blocklisting when the protocol exposes one.
/// </summary>
public sealed record AcquisitionResource
{
    public required AcquisitionProtocol Protocol { get; init; }
    public required string Locator { get; init; }
    public string? SourceId { get; init; }

    public static AcquisitionResource Torrent(string magnet, string? infoHash = null) => new()
    {
        Protocol = AcquisitionProtocol.Torrent,
        Locator = magnet,
        SourceId = infoHash
    };

    public static AcquisitionResource DirectAudio(string locator, string sourceId) => new()
    {
        Protocol = AcquisitionProtocol.DirectAudio,
        Locator = locator,
        SourceId = sourceId
    };

    /// <summary>Protocol-qualified identity used by the shared blocklist. The qualification prevents a
    /// provider id from colliding with an info hash or a future Usenet article id.</summary>
    public static string BlocklistKey(AcquisitionProtocol protocol, string sourceId) =>
        $"{(int)protocol}:{sourceId.Trim().ToLowerInvariant()}";
}
