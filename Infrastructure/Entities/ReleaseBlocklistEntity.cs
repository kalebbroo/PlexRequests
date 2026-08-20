using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// A release that failed and must not be grabbed again.
///
/// Without this, a request whose download failed would re-search, rank the same broken torrent top again,
/// and fail again — indefinitely, on the retry backoff. The blocklist is what turns "never dead-end" into
/// actual progress rather than a loop.
///
/// Keyed on the info hash, which is now available for every candidate (derived from the magnet when the
/// indexer doesn't report one). The normalised release name is a secondary key for the real case where the
/// same broken release is re-uploaded under a new hash.
/// </summary>
public class ReleaseBlocklistEntity
{
    public int Id { get; set; }

    /// <summary>Lower-case hex info hash. The primary identity.</summary>
    [MaxLength(40)] public string? InfoHash { get; set; }

    /// <summary>Protocol-qualified source identity. InfoHash remains populated for torrent compatibility.</summary>
    public AcquisitionProtocol Protocol { get; set; } = AcquisitionProtocol.Torrent;
    [MaxLength(256)] public string? SourceId { get; set; }

    /// <summary>Lower-cased, punctuation-stripped release name — catches a re-upload under a new hash.</summary>
    [MaxLength(512)] public string? NormalizedReleaseName { get; set; }

    /// <summary>How widely this block applies.</summary>
    public BlocklistScope Scope { get; set; } = BlocklistScope.Request;

    public int? MediaRequestId { get; set; }
    public int MediaId { get; set; }
    public MediaType MediaType { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public int? IndexerId { get; set; }

    public BlocklistReason Reason { get; set; }
    [MaxLength(1000)] public string? Detail { get; set; }

    /// <summary>Display name, so the admin panel doesn't show a bare hash.</summary>
    [MaxLength(512)] public string? ReleaseName { get; set; }

    public DateTime BlockedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Null = permanent. Set for blocks that should lapse (e.g. a transient stall).</summary>
    public DateTime? ExpiresAt { get; set; }
    public int? BlockedByUserId { get; set; }

    /// <summary>Case/punctuation-insensitive form of a release name, for the secondary match.</summary>
    public static string Normalize(string releaseName) =>
        new string(releaseName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
