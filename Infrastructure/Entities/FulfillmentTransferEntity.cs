using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// One transfer backing one fulfillment job — the durable link between what we asked for and what the
/// acquisition backend is actually doing. The physical table and two column names remain legacy-named so
/// the migration is additive and cannot strand in-flight production downloads.
///
/// This did not exist. The link lived in the downloader's local JSON file and in an in-memory telemetry
/// store, so it was lost whenever the worker was replaced, and nothing could ever answer "which job owns
/// this torrent?" from the database. The observable result on the live box: a job marked
/// "Deferred — no acceptable release found yet" while sixty-seven torrents it had added sat in Deluge,
/// nine of them finished, none of them imported, and the job re-searching on a backoff forever.
///
/// TransferId and SourceId stay separate deliberately: the first is the backend join key, while the second
/// is the source's stable identity for dedupe/blocklisting. They are not interchangeable for every protocol.
/// </summary>
public class FulfillmentTransferEntity
{
    public int Id { get; set; }
    public int FulfillmentJobId { get; set; }

    /// <summary>The backend's own id. Join key — never computed by us.</summary>
    [MaxLength(64)] public string TransferId { get; set; } = string.Empty;
    public AcquisitionProtocol Protocol { get; set; } = AcquisitionProtocol.Torrent;
    /// <summary>Stable source identity (the info hash for torrents), for dedupe and blocklisting.</summary>
    [MaxLength(64)] public string? SourceId { get; set; }
    [MaxLength(512)] public string? ReleaseName { get; set; }
    [MaxLength(128)] public string? Source { get; set; }
    public int? IndexerId { get; set; }

    // --- What this torrent is meant to satisfy -----------------------------------------------------
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public bool IsPack { get; set; }
    /// <summary>For a pack restricted to specific episodes, which ones we actually want.</summary>
    [MaxLength(512)] public string? NeededEpisodesCsv { get; set; }
    public int Resolution { get; set; }

    // --- Last observed state, written by the reconciler --------------------------------------------
    public TransferTrackingState State { get; set; } = TransferTrackingState.Active;
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
