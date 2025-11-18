using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

public class MediaRequestEntity
{
    public int Id { get; set; }
    public int MediaId { get; set; }
    public MediaType MediaType { get; set; }

    [MaxLength(512)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? PosterUrl { get; set; }

    public RequestStatus Status { get; set; } = RequestStatus.Pending;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    // Foreign key to User
    public int? RequestedByUserId { get; set; }

    // Keep for backward compatibility, but should migrate to UserId
    [MaxLength(128)]
    public string? RequestedBy { get; set; }
    public int? RequestedByUserId { get; set; }  // Foreign key to UserEntity
    public DateTime? ApprovedAt { get; set; }
    public DateTime? AvailableAt { get; set; }

    [MaxLength(1000)]
    public string? DenialReason { get; set; }

    [MaxLength(2000)]
    public string? RequestNote { get; set; }

    // TV specific selection
    public bool RequestAllSeasons { get; set; }

    [MaxLength(256)]
    public string? RequestedSeasonsCsv { get; set; }

    // Navigation property
    [ForeignKey(nameof(RequestedByUserId))]
    public UserEntity? RequestedByUser { get; set; }
}
