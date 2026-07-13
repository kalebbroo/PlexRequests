using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// Global, admin-configured download-selection preferences. A single row (the <see cref="IsSingleton"/>
/// record) governs how the out-of-process downloader ranks releases and chooses between full-season packs
/// and individual episodes. Served to the downloader over the secured /api/fulfillment/config endpoint.
/// Not exposed to end users.
/// </summary>
public class DownloadPreferencesEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>Marks the one-and-only settings row (mirrors the default-quality-rule singleton pattern).</summary>
    public bool IsSingleton { get; set; } = true;

    public SeasonPackStrategy SeasonPackStrategy { get; set; } = SeasonPackStrategy.PreferPack;
    public bool AllowEpisodeFallback { get; set; } = true;
    public int MaxEpisodesForFanout { get; set; } = 30;

    public int MinSeeders { get; set; } = 1;
    public double MaxSizeGb { get; set; } = 25;
    public double MaxSeasonPackSizeGb { get; set; } = 80;

    [MaxLength(1024)] public string? PreferredGroupsCsv { get; set; }
    public bool PreferX265 { get; set; } = true;
    public bool PreferHdr { get; set; }
    public bool PreferHigherQualitySource { get; set; } = true;
    public bool EnforceQualityFloor { get; set; } = true;

    /// <summary>When a user requests an entire series, automatically mark it monitored so newly-aired
    /// episodes are auto-downloaded. Admins can turn this off to require monitoring to be enabled manually.</summary>
    public bool AutoMonitorEntireSeriesRequests { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
