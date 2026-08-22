using System.ComponentModel.DataAnnotations;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>A reusable, live set of request permissions and rolling quotas.</summary>
public class UserGroupEntity
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? Description { get; set; }

    public int Permissions { get; set; }
    public int? MovieRequestLimit { get; set; }
    public int MovieRequestLimitDays { get; set; } = 7;
    public int? TvRequestLimit { get; set; }
    public int TvRequestLimitDays { get; set; } = 7;
    public int? MusicRequestLimit { get; set; }
    public int MusicRequestLimitDays { get; set; } = 7;
    [MaxLength(256)]
    public string? AllowedQualityProfileIdsCsv { get; set; }
    public bool IsSystem { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<UserProfileEntity> Members { get; set; } = new List<UserProfileEntity>();
}
