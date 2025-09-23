using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

public class MediaRequestEntity
{
    public int Id { get; set; }
    public int MediaId { get; set; }
    public MediaType MediaType { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? PosterUrl { get; set; }
    public RequestStatus Status { get; set; } = RequestStatus.Pending;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public string? RequestedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? AvailableAt { get; set; }
    public string? DenialReason { get; set; }
    public string? RequestNote { get; set; }
    // TV specific selection
    public bool RequestAllSeasons { get; set; }
    public string? RequestedSeasonsCsv { get; set; }
}
