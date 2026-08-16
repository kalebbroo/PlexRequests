using System.ComponentModel.DataAnnotations;

namespace PlexRequestsHosted.Infrastructure.Entities;

public sealed class FirefoxCapturePairingEntity
{
    public Guid Id { get; set; }
    public int IndexerId { get; set; }
    public IndexerEntity? Indexer { get; set; }
    [MaxLength(64)] public string CodeHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}
