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

    // When true, this user's requests skip the pending queue and are approved automatically.
    // Admins are always auto-approved regardless of this flag.
    public bool AutoApprove { get; set; }

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

    /// <summary>This user's pre-selected quality profile per media type; null falls back to the system
    /// default profile. The two raw quality ints above are what the seeder uses to guess these.</summary>
    public int? DefaultQualityProfileIdMovie { get; set; }
    public int? DefaultQualityProfileIdTv { get; set; }

    /// <summary>Comma-separated profile ids this user may choose from. Null/empty means "any profile marked
    /// user-selectable" — this narrows that further, so a member can be capped below what admins can pick.</summary>
    [MaxLength(256)] public string? AllowedQualityProfileIdsCsv { get; set; }
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

    // Discord integration fields
    [MaxLength(64)]
    public string? DiscordUserId { get; set; }
    [MaxLength(128)]
    public string? DiscordUsername { get; set; }
    public bool DiscordDmOptIn { get; set; }
}
