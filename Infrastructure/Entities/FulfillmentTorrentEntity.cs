using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// One torrent backing one fulfillment job — the durable link between what we asked for and what the
/// download client is actually doing.
///
/// This did not exist. The link lived in the downloader's local JSON file and in an in-memory telemetry
/// store, so it was lost whenever the worker was replaced, and nothing could ever answer "which job owns
/// this torrent?" from the database. The observable result on the live box: a job marked
/// "Deferred — no acceptable release found yet" while sixty-seven torrents it had added sat in Deluge,
/// nine of them finished, none of them imported, and the job re-searching on a backoff forever.
///
/// Two identifiers are kept deliberately. <see cref="TorrentId"/> is whatever the client calls it and is
/// the join key; <see cref="InfoHash"/> is derived from the magnet and is what the blocklist matches on.
/// For a v2 or hybrid torrent these are different values, so collapsing them would silently break one use
/// or the other.
/// </summary>
public class FulfillmentTorrentEntity
{
    public int Id { get; set; }
    public int FulfillmentJobId { get; set; }

    /// <summary>The download client's own id. Join key — never computed by us, always taken from the client.</summary>
    [MaxLength(64)] public string TorrentId { get; set; } = string.Empty;
    /// <summary>v1 infohash from the magnet, for blocklisting. May differ from <see cref="TorrentId"/>.</summary>
    [MaxLength(64)] public string? InfoHash { get; set; }
    [MaxLength(512)] public string? ReleaseName { get; set; }

    // --- What this torrent is meant to satisfy -----------------------------------------------------
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public bool IsPack { get; set; }
    /// <summary>For a pack restricted to specific episodes, which ones we actually want.</summary>
    [MaxLength(512)] public string? NeededEpisodesCsv { get; set; }
    public int Resolution { get; set; }

    // --- Last observed state, written by the reconciler --------------------------------------------
    public TorrentTrackingState State { get; set; } = TorrentTrackingState.Active;
    /// <summary>0-100.</summary>
    public double Progress { get; set; }
    public int Seeds { get; set; }
    public int Peers { get; set; }
    public double DownloadRateBytesPerSec { get; set; }
    public long TotalSizeBytes { get; set; }

    /// <summary>
    /// The last time <see cref="Progress"/> actually changed — not the last time we looked. Stall
    /// detection keys off this, and conflating the two would mean a torrent that is polled often but
    /// never advances looks perpetually healthy.
    /// </summary>
    public DateTime? ProgressChangedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    [MaxLength(512)] public string? TrackerStatus { get; set; }
    [MaxLength(512)] public string? FailReason { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ImportedAt { get; set; }

    [ForeignKey(nameof(FulfillmentJobId))]
    public FulfillmentJobEntity? Job { get; set; }
}
