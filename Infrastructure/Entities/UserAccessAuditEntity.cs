using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// Immutable, human-readable record of a semantic user-access mutation. Target and actor names are copied
/// deliberately so the security history remains understandable even if a group is later renamed or deleted.
/// </summary>
public sealed class UserAccessAuditEntity
{
    public long Id { get; set; }
    public UserAccessAuditTarget TargetType { get; set; }
    public int TargetId { get; set; }
    [MaxLength(128)] public string TargetName { get; set; } = string.Empty;
    public UserAccessAuditAction Action { get; set; }
    public int? ActorUserId { get; set; }
    [MaxLength(128)] public string ActorUsername { get; set; } = string.Empty;
    [MaxLength(2048)] public string Summary { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
