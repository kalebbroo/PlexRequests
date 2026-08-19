using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// One internal identity for a requestable catalog item. Consumers reference this row instead of assuming
/// that every provider has a TMDb integer. Provider aliases make Plex, TMDb, TVDb and MusicBrainz ids all
/// resolve to the same item without copying provider-specific columns into every table.
/// </summary>
public class MediaIdentityEntity
{
    public int Id { get; set; }
    public MediaType MediaType { get; set; }
    public MediaKind Kind { get; set; }

    [MaxLength(32)] public string PrimaryProvider { get; set; } = string.Empty;
    [MaxLength(128)] public string PrimaryId { get; set; } = string.Empty;
    [MaxLength(256)] public string StableKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<MediaExternalIdentifierEntity> ExternalIdentifiers { get; set; }
        = new List<MediaExternalIdentifierEntity>();
}

/// <summary>A provider alias for a canonical media identity.</summary>
public class MediaExternalIdentifierEntity
{
    public int Id { get; set; }
    public int MediaIdentityId { get; set; }
    public MediaType MediaType { get; set; }
    public MediaKind Kind { get; set; }
    [MaxLength(32)] public string Provider { get; set; } = string.Empty;
    [MaxLength(128)] public string ExternalId { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }

    public MediaIdentityEntity? MediaIdentity { get; set; }
}
