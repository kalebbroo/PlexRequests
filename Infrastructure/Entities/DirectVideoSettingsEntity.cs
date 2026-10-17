using System.ComponentModel.DataAnnotations;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>Global switch and official-channel allowlist for the YouTube episode source. The API key is deliberately
/// absent: it lives in configuration, so a database export never carries a credential.</summary>
public sealed class DirectVideoSettingsEntity
{
    [Key]
    public int Id { get; set; }
    public bool IsSingleton { get; set; } = true;
    public bool Enabled { get; set; }
    /// <summary>Newline-separated official YouTube channel ids.</summary>
    [MaxLength(4000)]
    public string ChannelAllowlist { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
