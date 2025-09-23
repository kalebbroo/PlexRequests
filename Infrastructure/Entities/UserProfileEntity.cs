using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PlexRequestsHosted.Infrastructure.Entities;

public class UserProfileEntity
{
    [Key]
    public int Id { get; set; }

    [ForeignKey(nameof(User))]
    public int UserId { get; set; }
    public UserEntity? User { get; set; }

    // Plex linkage
    [MaxLength(64)]
    public string? PlexId { get; set; }
    [MaxLength(128)]
    public string? PlexUsername { get; set; }

    // Roles: simple comma-separated list (e.g., "User,Admin")
    [MaxLength(256)]
    public string Roles { get; set; } = "User";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    // Preferences (explicit columns for queryability)
    public bool ThemeDarkMode { get; set; } = true;
    [MaxLength(8)]
    public string Language { get; set; } = "en";
    [MaxLength(8)]
    public string Region { get; set; } = "US";
    public bool ShowAdultContent { get; set; } = true;
    public int DefaultSort { get; set; } = 0; // maps to Shared.Enums.SortOrder
    public int DefaultQualityMovie { get; set; } = 1080; // maps to Shared.Enums.Quality
    public int DefaultQualityTV { get; set; } = 1080;   // maps to Shared.Enums.Quality
    public bool AutoplayTrailers { get; set; } = false;
    public bool WatchedBadges { get; set; } = true;

    [MaxLength(16)]
    public string PreferredProvider { get; set; } = "TMDb";

    // Access/limits (defaults visible to user but not editable unless admin)
    public int? MovieRequestLimit { get; set; } = 5;
    public int? TvRequestLimit { get; set; } = 3;
    public int? MusicRequestLimit { get; set; } = 10;

    [MaxLength(16)]
    public string WhitelistStatus { get; set; } = "Unknown"; // Unknown | Approved | Denied

    // Connections
    [MaxLength(64)]
    public string? PreferredServerMachineId { get; set; }
    [MaxLength(128)]
    public string? PreferredServerName { get; set; }
}
