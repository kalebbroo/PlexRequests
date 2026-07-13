using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// A user-reported problem with a title that's already on Plex (bad quality, broken playback, wrong
/// version, etc.). This is the sanctioned escape hatch that lets a user get an on-Plex title re-fetched
/// even though normal requests of available content are blocked. Admins triage these and can trigger a
/// forced re-download. Optional season/episode narrows the report to part of a show.
/// </summary>
public class MediaIssueEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int MediaId { get; set; }            // TMDb id
    public MediaType MediaType { get; set; }

    [MaxLength(512)] public string Title { get; set; } = string.Empty;
    public string? PosterUrl { get; set; }

    public int? ReportedByUserId { get; set; }
    [MaxLength(128)] public string? ReportedBy { get; set; }

    [MaxLength(64)] public string Reason { get; set; } = "Other";   // Bad quality / Playback broken / Wrong version / ...
    [MaxLength(2048)] public string? Detail { get; set; }

    public int? SeasonNumber { get; set; }      // optional, TV
    public int? EpisodeNumber { get; set; }     // optional, TV

    public IssueStatus Status { get; set; } = IssueStatus.Open;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    [MaxLength(128)] public string? ResolvedBy { get; set; }
}
