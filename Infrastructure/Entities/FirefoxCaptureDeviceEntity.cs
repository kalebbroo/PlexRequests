using System.ComponentModel.DataAnnotations;

namespace PlexRequestsHosted.Infrastructure.Entities;

public sealed class FirefoxCaptureDeviceEntity
{
    public Guid Id { get; set; }
    public Guid PairingId { get; set; }
    public FirefoxCapturePairingEntity? Pairing { get; set; }
    public int IndexerId { get; set; }
    public IndexerEntity? Indexer { get; set; }
    [MaxLength(64)] public string TokenHash { get; set; } = string.Empty;
    [MaxLength(128)] public string Name { get; set; } = string.Empty;
    [MaxLength(32)] public string ExtensionVersion { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? LastCaptureAt { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public bool CaptureEnabled { get; set; }
    public int QueuedUploads { get; set; }
    public int FailedUploads { get; set; }
    public int PendingDetails { get; set; }
    public int AttentionDetails { get; set; }
    public DateTime? HydrationPausedUntil { get; set; }
    public long BatchesReceived { get; set; }
    public long ItemsReceived { get; set; }
    public int? LastParserVersion { get; set; }
    [MaxLength(2048)] public string? LastPageUrl { get; set; }
    public DateTime? RevokedAt { get; set; }
}
