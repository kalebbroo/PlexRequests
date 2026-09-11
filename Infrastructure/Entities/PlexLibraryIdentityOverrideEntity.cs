using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// An administrator-confirmed provider identity for a Plex inventory title when Plex did not expose a
/// usable guid. This is deliberately separate from <see cref="PlexMappingEntity"/>: inventory scans own
/// Plex mappings and may prune them, whereas this explicit correction must remain stable until changed.
/// </summary>
public sealed class PlexLibraryIdentityOverrideEntity
{
    public int Id { get; set; }

    /// <summary>Canonical <c>plex:{sectionKey}:{ownerRatingKey}</c> identity.</summary>
    [Required, MaxLength(256)] public string InventoryKey { get; set; } = string.Empty;
    [Required, MaxLength(64)] public string ExternalKey { get; set; } = string.Empty;
    public MediaType MediaType { get; set; }
    [MaxLength(512)] public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public DateTime ConfirmedAt { get; set; } = DateTime.UtcNow;
}
