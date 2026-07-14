using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// Global, admin-configured settings for how the downloader organizes finished downloads into the Plex
/// library — root paths, naming templates, and transfer behavior. A single row (the
/// <see cref="IsSingleton"/> record), mirroring <see cref="DownloadPreferencesEntity"/>'s pattern.
/// Served to the downloader over the secured /api/fulfillment/library-config endpoint.
/// </summary>
public class LibraryOrganizationPreferencesEntity
{
    [Key]
    public int Id { get; set; }

    public bool IsSingleton { get; set; } = true;

    [MaxLength(1024)] public string MoviePath { get; set; } = string.Empty;
    [MaxLength(1024)] public string TvPath { get; set; } = string.Empty;

    [MaxLength(512)] public string MovieTemplate { get; set; } = "{Title} ({Year})/{Title} ({Year}){Ext}";
    [MaxLength(512)] public string TvEpisodeTemplate { get; set; } = "{ShowTitle} ({Year})/Season {Season:00}/{ShowTitle} - s{Season:00}e{Episode:00} - {EpisodeTitle}{Ext}";
    [MaxLength(512)] public string SeasonPackFolderTemplate { get; set; } = "{ShowTitle} ({Year})/Season {Season:00}";

    /// <summary>Serialized List&lt;LibraryRootRuleDto&gt;. Null/empty = no routing rules.</summary>
    public string? LibraryRootRulesJson { get; set; }

    public TransferMode TransferMode { get; set; } = TransferMode.Hardlink;
    public bool ExtractArchives { get; set; } = true;
    public bool SplitSeasonPacks { get; set; } = true;
    public bool KeepSubtitles { get; set; } = true;
    [MaxLength(256)] public string SubtitleExtensionsCsv { get; set; } = ".srt,.ass,.ssa,.sub,.vtt";
    [MaxLength(256)] public string VideoExtensionsCsv { get; set; } = ".mkv,.mp4,.avi,.m4v,.ts,.mov,.wmv,.m2ts";
    public double MinVideoFileSizeMb { get; set; } = 50;
    public bool DeleteSourceAfterImport { get; set; } = false;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
