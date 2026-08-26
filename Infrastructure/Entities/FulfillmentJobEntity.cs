using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// A unit of work handed to the out-of-process downloader when an admin approves a request.
/// The web app only enqueues and tracks state; the downloader claims jobs and reports back via
/// the secured /api/fulfillment and /api/requests/{id}/(fulfilled|failed) endpoints.
/// </summary>
public class FulfillmentJobEntity
{
    public int Id { get; set; }

    public int MediaRequestId { get; set; }
    public int? MediaIdentityId { get; set; }

    public int MediaId { get; set; }
    public MediaType MediaType { get; set; }
    public MediaKind MediaKind { get; set; }
    public RequestScopeKind RequestScopeKind { get; set; }

    /// <summary>Serialized <c>MusicAcquisitionContextDto</c>; null for non-music jobs.</summary>
    public string? AcquisitionContextJson { get; set; }

    [MaxLength(512)]
    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    // External identifiers for indexer search (MediaId is the TMDb id for the default provider).
    public int? TmdbId { get; set; }
    [MaxLength(32)]
    public string? ImdbId { get; set; }
    public int? TvdbId { get; set; }

    // Transitional non-TMDb mirrors for worker v1. The v2 DTO also carries a provider-neutral MediaRef.
    [MaxLength(128)] public string? ExternalId { get; set; }
    [MaxLength(32)] public string? ExternalSource { get; set; }

    [MaxLength(256)]
    public string? RequestedSeasonsCsv { get; set; }

    // Episode-level targets, format "S1E1,S2E5". Empty ⇒ fall back to seasons/whole title.
    [MaxLength(4096)]
    public string? RequestedEpisodesCsv { get; set; }

    // Per-season fan-out targets (JSON-serialized List<SeasonTarget>): each missing season's total episode
    // count and which episode numbers are still missing from Plex. Lets the downloader prefer a pack and
    // fall back to precisely the missing episodes. Null/empty ⇒ pack-only (metadata unavailable at enqueue).
    public string? SeasonTargetsJson { get; set; }

    public Quality Quality { get; set; }

    // Classification snapshot taken at enqueue time, so the downloader can route/organize without
    // re-fetching metadata: genres for admin-configured "GenreContains" routing rules, and the shared
    // Animation+Japanese-origin heuristic result for anime-specific routing rules.
    [MaxLength(512)] public string? GenresCsv { get; set; }
    public bool IsAnime { get; set; }

    // Immutable library routing snapshot. Rules may change while a download is in flight; imports must
    // still land in the destination approved for this job.
    [MaxLength(64)] public string? LibraryDestinationId { get; set; }
    [MaxLength(128)] public string? LibraryDestinationName { get; set; }
    public LibraryContentKind? LibraryDestinationKind { get; set; }
    [MaxLength(1024)] public string? LibraryDestinationRootPath { get; set; }
    [MaxLength(128)] public string? LibraryPlexSectionId { get; set; }
    [MaxLength(512)] public string? LibraryDestinationTemplate { get; set; }
    [MaxLength(512)] public string? LibrarySeasonPackFolderTemplate { get; set; }

    public FulfillmentStatus Status { get; set; } = FulfillmentStatus.Queued;
    public int Attempts { get; set; }
    public int Progress { get; set; } // 0-100

    // --- Quality-upgrade state -----------------------------------------------------------------------
    /// <summary>True when this job is an automatic quality upgrade of already-available content (the
    /// UpgradeScanJob enqueued it because the request was downloaded below its preferred quality). Upgrade
    /// jobs enforce the quality floor unconditionally (never a downgrade/side-grade) and, on success,
    /// replace <see cref="ReplacePathsJson"/> rather than adding fresh content.</summary>
    public bool IsUpgrade { get; set; }
    /// <summary>An issue-triggered replacement. It shares upgrade-safe file swapping, but it remains
    /// deferred and keeps searching when no acceptable alternative is currently available.</summary>
    public bool IsReplacement { get; set; }
    public int? MediaIssueId { get; set; }
    /// <summary>For an upgrade job: JSON array of the existing library file paths this upgrade supersedes.
    /// The downloader deletes them after the better release imports; the web app removes their audit rows.</summary>
    public string? ReplacePathsJson { get; set; }

    /// <summary>Set once when a long-deferred job is escalated to admins (see <see cref="DeferCount"/>), so
    /// the "still searching, needs attention" notification fires exactly once rather than every retry.</summary>
    public bool Escalated { get; set; }

    // --- Deferred-retry state (for a request whose release isn't findable yet) ---------------------
    /// <summary>When a Deferred job becomes claimable again. The downloader's claim query skips deferred
    /// jobs until this passes, so a "not found yet" request is re-searched on a backoff instead of failing.</summary>
    public DateTime? NextRetryAt { get; set; }
    /// <summary>How many times this job has been deferred (no release found yet) — drives the retry backoff
    /// and is shown in the UI. Reset when a real download attempt begins.</summary>
    public int DeferCount { get; set; }

    /// <summary>
    /// The quality profile this job searches against, snapshotted from the request at enqueue so a mid-flight
    /// profile edit can't change what an in-progress job is hunting for. Nullable during the transition.
    /// </summary>
    public int? QualityProfileId { get; set; }

    /// <summary>Serialized MediaLanguagePolicyDto captured at enqueue. Unlike the profile id, this remains
    /// unchanged if an admin edits the profile while a transfer is in flight.</summary>
    public string? MediaLanguagePolicyJson { get; set; }

    /// <summary>Serialized SeriesEpisodeOrderProfileDto captured at enqueue. Imports and ranking must use
    /// this snapshot rather than mutable admin configuration.</summary>
    public string? EpisodeOrderProfileJson { get; set; }

    /// <summary>
    /// Searches that returned candidates but nothing acceptable. Distinct from <see cref="Attempts"/>, which
    /// counts every claim including re-searches of an already-deferred job — using that to decide when to
    /// relax the quality floor meant a single deferral dropped the floor permanently. This counter only moves
    /// when a search genuinely found nothing good enough.
    /// </summary>
    public int EmptySearchCount { get; set; }

    // --- Manual grab ---------------------------------------------------------------------------------
    /// <summary>An admin picked this exact release from an interactive search. The downloader skips
    /// searching and ranking entirely — the whole point of forcing a grab is to override that judgement.</summary>
    public bool IsManualGrab { get; set; }
    [MaxLength(2048)] public string? ForcedMagnet { get; set; }
    [MaxLength(512)] public string? ForcedReleaseName { get; set; }
    public int? ForcedIndexerId { get; set; }
    /// <summary>Movie release / show first-air date, snapshotted at enqueue. Lets the retry cadence back off
    /// to ~daily while the title isn't out yet instead of hammering indexers.</summary>
    public DateTime? ReleaseDate { get; set; }

    [MaxLength(128)]
    public string? ClaimedBy { get; set; }

    [MaxLength(2000)]
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClaimedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>Last time the worker touched this job (claim/progress). Used by the stale-claim reaper.</summary>
    public DateTime? LastUpdatedAt { get; set; }

    [ForeignKey(nameof(MediaRequestId))]
    public MediaRequestEntity? MediaRequest { get; set; }

    [ForeignKey(nameof(MediaIdentityId))]
    public MediaIdentityEntity? MediaIdentity { get; set; }
}
