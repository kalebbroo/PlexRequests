using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// A title currently trending on an indexer, resolved to real metadata and cached for the home page's
/// "Recommended" row.
///
/// Cached rather than fetched live for two reasons. The obvious one is that a page load must never wait on
/// (or hammer) a torrent site. The load-bearing one is that the web app has no business talking to indexers
/// at all: it sits outside the VPN tunnel, so scraping from there would egress tracker traffic on the
/// user's real IP. The downloader browses from behind the tunnel and pushes results here.
/// </summary>
public class RecommendedItemEntity
{
    public int Id { get; set; }

    /// <summary>Title as parsed from the release name, before metadata resolution.</summary>
    [MaxLength(512)] public string SourceTitle { get; set; } = string.Empty;
    public int? SourceYear { get; set; }

    public MediaType MediaType { get; set; }

    /// <summary>Resolved TMDb id. Rows that can't be resolved are dropped rather than stored — an
    /// unresolvable release name has no poster and nothing to request.</summary>
    public int TmdbId { get; set; }

    [MaxLength(512)] public string Title { get; set; } = string.Empty;
    [MaxLength(1024)] public string? PosterUrl { get; set; }
    public int? Year { get; set; }

    /// <summary>Which indexer this came from, so a swapped source doesn't leave stale rows behind.</summary>
    public int SourceIndexerId { get; set; }

    /// <summary>Position in the source's newest-first listing; lower is newer.</summary>
    public int Rank { get; set; }

    /// <summary>When the feed last reported this title. Drives the staleness check that hides the row.</summary>
    public DateTime SeenAt { get; set; } = DateTime.UtcNow;
}
