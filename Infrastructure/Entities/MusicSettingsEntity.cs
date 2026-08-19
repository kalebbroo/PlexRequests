using System.ComponentModel.DataAnnotations;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>Global switch for the optional music catalog. Acquisition stays controlled by the music
/// module's capability state, so enabling metadata can never enqueue an incomplete download path.</summary>
public sealed class MusicSettingsEntity
{
    [Key]
    public int Id { get; set; }
    public bool IsSingleton { get; set; } = true;
    public bool CatalogEnabled { get; set; }
    [MaxLength(32)]
    public string MetadataProvider { get; set; } = "musicbrainz";
    public bool RequestsEnabled { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
