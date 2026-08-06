using System.ComponentModel.DataAnnotations;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// A named scoring rule matched against releases — "has Dolby Vision", "is a bad dual-audio group",
/// "is an upscale". Formats carry no score of their own; the score is per (profile, format), so the same
/// rule can be desirable in one profile and disqualifying in another.
/// </summary>
public class CustomFormatEntity
{
    public int Id { get; set; }

    [MaxLength(128)] public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    /// <summary>Seeded by the app. Editable, but re-created if deleted.</summary>
    public bool IsSystem { get; set; }

    /// <summary>Include this format's name when renaming imported files. Not yet honoured by the organizer.</summary>
    public bool IncludeWhenRenaming { get; set; }

    /// <summary>
    /// The conditions, as JSON. Same reasoning as a profile's tier list: always loaded whole, never queried
    /// by individual specification, and the codebase already stores owned ordered collections this way.
    /// </summary>
    public string SpecificationsJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>What one custom format is worth inside one quality profile.</summary>
public class CustomFormatScoreEntity
{
    public int Id { get; set; }
    public int QualityProfileId { get; set; }
    public int CustomFormatId { get; set; }
    public int Score { get; set; }
}
