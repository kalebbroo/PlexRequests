using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

public class MediaRequestEntity
{
    public int Id { get; set; }
    public int MediaId { get; set; }
    public MediaType MediaType { get; set; }
    public string Title { get; set; } = string.Empty;
    public RequestStatus Status { get; set; } = RequestStatus.Pending;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public string? RequestedBy { get; set; }
}
