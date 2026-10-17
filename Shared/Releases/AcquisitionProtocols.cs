using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared.Releases;

/// <summary>Classifies acquisition protocols by what they do rather than by name. A remote direct source fetches a
/// frozen third-party media item: it has no info hash, no seeders, and no size until fetched, and it recovers from
/// transient extractor failures on a cooldown. Each new direct provider joins these rules by joining this set.</summary>
public static class AcquisitionProtocols
{
    public static bool IsRemoteDirect(this AcquisitionProtocol protocol) =>
        protocol is AcquisitionProtocol.DirectAudio or AcquisitionProtocol.DirectVideo;
}
