using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared.Releases;

/// <summary>
/// How long a failed release stays blocked. A content failure says something permanent about the release, so it is
/// blocked for good. An infrastructure failure says nothing about the release itself: a torrent with no peers stalls, a
/// finished path cannot be read, the client reports an error. Those blocks expire, so the same link is tried again
/// instead of being ignored forever on a false verdict.
/// </summary>
public static class BlocklistExpiry
{
    public static readonly TimeSpan TransientDuration = TimeSpan.FromHours(6);

    /// <summary>Null means permanent. A value means the block lapses at that time and the release may be tried again.</summary>
    public static DateTime? For(BlocklistReason reason, AcquisitionProtocol protocol, DateTime now)
    {
        // A remote direct source has always expired its blocks: its extractor fails transiently and recovers.
        if (protocol.IsRemoteDirect()) return now + TransientDuration;

        return reason switch
        {
            BlocklistReason.Stalled or BlocklistReason.TorrentError or BlocklistReason.PathUnresolvable
                or BlocklistReason.DownloadFailed or BlocklistReason.ImportFailed => now + TransientDuration,
            // Wrong content, a policy mismatch, an ambiguous mapping, and an admin block are decisions about the release.
            _ => null
        };
    }
}
