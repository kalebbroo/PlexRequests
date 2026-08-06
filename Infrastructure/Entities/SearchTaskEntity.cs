using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// A unit of work the web app hands to the downloader: search these indexers now and tell me everything you
/// found, including what you'd have rejected.
///
/// It exists as a table rather than a direct call because the web app cannot reach the downloader. The
/// downloader runs as a worker with no listening port and, in the supported VPN topology, shares gluetun's
/// network namespace — so every interaction has to be something the worker pulls. The worker polls this
/// table on a short interval and posts results back over the same authenticated API it already uses.
///
/// Rows are transient: results are capped and the whole row is deleted once <see cref="ExpiresAt"/> passes.
/// </summary>
public class SearchTaskEntity
{
    public int Id { get; set; }

    public SearchTaskKind Kind { get; set; } = SearchTaskKind.InteractiveSearch;
    public SearchTaskStatus Status { get; set; } = SearchTaskStatus.Pending;

    public int? RequestedByUserId { get; set; }

    // ---- What to search for -------------------------------------------------------------------------
    /// <summary>The request this search is for, when it's scoped to one. Null for an ad-hoc search.</summary>
    public int? MediaRequestId { get; set; }
    public MediaType MediaType { get; set; }
    public int MediaId { get; set; }
    [MaxLength(512)] public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    [MaxLength(32)] public string? ImdbId { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }

    /// <summary>Profile to evaluate against, so the results show the same accept/reject decisions the
    /// automatic search would make.</summary>
    public int? QualityProfileId { get; set; }

    /// <summary>Restrict to one indexer — used by the capabilities test.</summary>
    public int? IndexerId { get; set; }

    /// <summary>Return rejected candidates too. On by default: seeing why a release wasn't chosen is the
    /// entire point of running one of these by hand.</summary>
    public bool IncludeRejected { get; set; } = true;

    // ---- Outcome ------------------------------------------------------------------------------------
    [MaxLength(128)] public string? ClaimedBy { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Serialized results. Capped by the service before storing — this table must not become a
    /// place where large blobs accumulate.</summary>
    public string? ResultsJson { get; set; }

    /// <summary>Per-indexer outcome breakdown, so "which indexers returned nothing" is answerable.</summary>
    public string? IndexerOutcomesJson { get; set; }

    /// <summary>Serialized <c>IndexerCapabilitiesDto</c> for a capabilities test; null for a search.</summary>
    public string? CapabilitiesJson { get; set; }

    [MaxLength(2000)] public string? Error { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this row becomes garbage. The scheduled reaper deletes anything past it.</summary>
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(30);
}
