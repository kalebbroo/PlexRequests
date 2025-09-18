namespace PlexRequestsHosted.Infrastructure.Entities;

public class WatchlistItemEntity
{
    public int Id { get; set; }
    public int MediaId { get; set; }
    public string Username { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
