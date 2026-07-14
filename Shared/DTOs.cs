using PlexRequestsHosted.Shared.Enums;
using System.ComponentModel.DataAnnotations;

namespace PlexRequestsHosted.Shared.DTOs;

public abstract class BaseDto
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class MediaCardDto : BaseDto
{
    [Required]
    public string Title { get; set; } = string.Empty;
    public string? Overview { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public int? Year { get; set; }
    public decimal? Rating { get; set; }
    public int? Runtime { get; set; }
    public MediaType MediaType { get; set; }
    public List<string> Genres { get; set; } = new();
    public string? Quality { get; set; }
    public bool IsAvailable { get; set; }
    public RequestStatus RequestStatus { get; set; }
    public string? PlexUrl { get; set; }
    public int TotalSeasons { get; set; }
    public int AvailableSeasons { get; set; }
    public string? ImdbId { get; set; }
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }
    // Provider-agnostic id for non-TMDb sources (MusicBrainz MBID, Plex ratingKey, ...) + its source key.
    public string? ExternalId { get; set; }
    public string? ExternalSource { get; set; }
}

public class MediaDetailDto : MediaCardDto
{
    public string? Tagline { get; set; }
    public List<string> Cast { get; set; } = new();
    public List<string> Directors { get; set; } = new();
    public List<string> Writers { get; set; } = new();
    public string? Studio { get; set; }
    public string? Network { get; set; }
    public string? ContentRating { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public DateTime? FirstAired { get; set; }
    public DateTime? LastAired { get; set; }
    public string? Status { get; set; }
    public List<SeasonDto> Seasons { get; set; } = new();
    public string? TrailerUrl { get; set; }
    public List<string> Languages { get; set; } = new();
    public List<string> Countries { get; set; } = new();
}

public class SeasonDto
{
    public int SeasonNumber { get; set; }
    public string? Name { get; set; }
    public int EpisodeCount { get; set; }
    public string? PosterUrl { get; set; }
    public DateTime? AirDate { get; set; }
    public bool IsAvailable { get; set; }
    public int AvailableEpisodes { get; set; }
}

public class EpisodeDto
{
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }
    public string? StillUrl { get; set; }
    public DateTime? AirDate { get; set; }
    public bool IsAvailable { get; set; }   // already on Plex
    public bool HasAired => AirDate.HasValue && AirDate.Value.Date <= DateTime.UtcNow.Date;
}

public class MediaRequestDto : BaseDto
{
    [Required]
    public int MediaId { get; set; }
    [Required]
    public MediaType MediaType { get; set; }
    [Required]
    public string Title { get; set; } = string.Empty;
    public string? PosterUrl { get; set; }
    public RequestStatus Status { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? AvailableAt { get; set; }
    public int RequestedByUserId { get; set; }
    public string RequestedByUsername { get; set; } = string.Empty;
    public int? ApprovedByUserId { get; set; }
    public string? ApprovedByUsername { get; set; }
    public Quality PreferredQuality { get; set; }
    public string? RequestNote { get; set; }
    public string? DenialReason { get; set; }
    public List<int> RequestedSeasons { get; set; } = new();
    public bool RequestAllSeasons { get; set; }
    public string? RequestedEpisodesCsv { get; set; }   // "S1E1,S2E5" — episode-level targets
    public bool Monitored { get; set; }                 // ongoing-series auto-download
    public string? ExternalId { get; set; }             // non-TMDb id (music MBID / Plex ratingKey)
    public string? ExternalSource { get; set; }
}

public class MediaIssueDto
{
    public int Id { get; set; }
    public int MediaId { get; set; }
    public MediaType MediaType { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? PosterUrl { get; set; }
    public int? ReportedByUserId { get; set; }
    public string? ReportedBy { get; set; }
    public string Reason { get; set; } = "Other";
    public string? Detail { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public IssueStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

// --- Music (Plex-native model, ported from PlexBot's artist/album/track hierarchy) ---
public class MusicArtistDto
{
    public string RatingKey { get; set; } = string.Empty;   // Plex id
    public string Name { get; set; } = string.Empty;
    public string? ArtworkUrl { get; set; }
    public string? Genre { get; set; }
    public string? Key { get; set; }                          // children path -> albums
}

public class MusicAlbumDto
{
    public string RatingKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? ArtworkUrl { get; set; }
    public string? Genre { get; set; }
    public string? Key { get; set; }                          // children path -> tracks
    public bool IsAvailable { get; set; }                     // present on Plex
}

public class MusicTrackDto
{
    public string RatingKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public int? DurationMs { get; set; }
}

public class MusicSearchResultDto
{
    public List<MusicArtistDto> Artists { get; set; } = new();
    public List<MusicAlbumDto> Albums { get; set; } = new();
    public List<MusicTrackDto> Tracks { get; set; } = new();
}

public class QualityRuleDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsDefault { get; set; }
    public MediaType? MatchMediaType { get; set; }
    public string? MatchGenre { get; set; }
    public int? MatchTmdbId { get; set; }
    public string? MatchLibrary { get; set; }
    public Quality TargetQuality { get; set; } = Quality.FullHD;
}

/// <summary>
/// Global, admin-configured download-selection preferences. A single record governs how the downloader
/// ranks releases and chooses between season packs and individual episodes. This is also the wire type
/// served by GET /api/fulfillment/config and consumed by the downloader.
/// </summary>
public class DownloadPreferencesDto
{
    public SeasonPackStrategy SeasonPackStrategy { get; set; } = SeasonPackStrategy.PreferPack;
    /// <summary>When a pack is wanted but none is acceptable, download the missing episodes individually.</summary>
    public bool AllowEpisodeFallback { get; set; } = true;
    /// <summary>Safety cap: if a season is missing more episodes than this and no pack exists, fail instead of fanning out.</summary>
    public int MaxEpisodesForFanout { get; set; } = 30;

    public int MinSeeders { get; set; } = 1;
    public double MaxSizeGb { get; set; } = 25;
    public double MaxSeasonPackSizeGb { get; set; } = 80;

    /// <summary>Comma-separated preferred release groups (case-insensitive) that get a scoring bonus.</summary>
    public string? PreferredGroupsCsv { get; set; }
    public bool PreferX265 { get; set; } = true;
    public bool PreferHdr { get; set; }
    public bool PreferHigherQualitySource { get; set; } = true;
    /// <summary>Treat the job's Quality as a hard floor (reject lower) rather than a soft preference.</summary>
    public bool EnforceQualityFloor { get; set; } = true;

    /// <summary>
    /// Minimum normalized token-overlap similarity (0-1) between a release name and the requested title
    /// for the release to be considered at all. Guards free-text-search indexers (1337x/ext.to/Nyaa)
    /// against accepting an unrelated/wrong-show/spam release just because it clears quality/seeder/size
    /// thresholds.
    /// </summary>
    public double MinTitleSimilarity { get; set; } = 0.5;

    /// <summary>When a user requests an entire series, automatically monitor it for new episodes.</summary>
    public bool AutoMonitorEntireSeriesRequests { get; set; } = true;
}

public class UserDto : BaseDto
{
    [Required]
    public string Username { get; set; } = string.Empty;
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public List<string> Roles { get; set; } = new();
    public bool AutoApprove { get; set; }
    public UserPreferencesDto Preferences { get; set; } = new();
    public UserStatsDto Stats { get; set; } = new();
    public string? PlexUsername { get; set; }
    public string? PlexId { get; set; }
    public bool PlexLinked { get; set; }
    public int? MovieRequestLimit { get; set; }
    public int? TvRequestLimit { get; set; }
    public int? MusicRequestLimit { get; set; }
}

// Wire types for the fulfillment worker API — shared by the web app (endpoints) and the downloader (client).
public record ClaimRequest(string? WorkerId, int? Max);
public record ProgressRequest(int Progress, string? WorkerId);
public record FailRequest(string? Reason);
public record RefreshLibraryRequest(MediaType MediaType);

public class FulfillmentJobDto
{
    public int Id { get; set; }
    public int MediaRequestId { get; set; }
    public int MediaId { get; set; }
    public MediaType MediaType { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public int? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public int? TvdbId { get; set; }
    public List<int> RequestedSeasons { get; set; } = new();
    /// <summary>Episode-level targets as (season, episode) pairs. Empty ⇒ use RequestedSeasons / whole title.</summary>
    public List<EpisodeRef> RequestedEpisodes { get; set; } = new();
    /// <summary>
    /// Per-season fan-out targets computed at enqueue: for each missing season, its total episode count and
    /// which episode numbers are still missing from Plex. Lets the downloader prefer a season pack and fall
    /// back to precisely the missing episodes. Empty ⇒ metadata unavailable, so the downloader is pack-only.
    /// </summary>
    public List<SeasonTarget> SeasonTargets { get; set; } = new();
    public Quality Quality { get; set; }
    /// <summary>Genres snapshotted at enqueue time (for admin-configured library-routing rules).</summary>
    public List<string> Genres { get; set; } = new();
    /// <summary>Animation+Japanese-origin heuristic result, snapshotted at enqueue time (see <see cref="PlexRequestsHosted.Shared.AnimeClassifier"/>).</summary>
    public bool IsAnime { get; set; }
    public FulfillmentStatus Status { get; set; }
    public int Attempts { get; set; }
    public int Progress { get; set; }
}

/// <summary>A single episode target for the downloader, e.g. season 2 episode 5.</summary>
public class EpisodeRef
{
    public int Season { get; set; }
    public int Episode { get; set; }
}

/// <summary>A season's fan-out target: its total episode count and the episode numbers still missing from Plex.</summary>
public class SeasonTarget
{
    public int Season { get; set; }
    public int EpisodeCount { get; set; }
    public List<int> MissingEpisodes { get; set; } = new();
}

public class NotificationDto
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public NotificationType Type { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? RelatedRequestId { get; set; }
}

public class UserPreferencesDto
{
    public bool DarkMode { get; set; } = true;
    public string Language { get; set; } = "en";
    public bool EmailNotifications { get; set; } = true;
    public bool PushNotifications { get; set; } = false;
    public Quality DefaultQuality { get; set; } = Quality.FullHD;
    public bool AutoPlayTrailers { get; set; } = false;
    public bool ShowAdultContent { get; set; } = true;
    public SortOrder DefaultSort { get; set; } = SortOrder.DateDescending;
}

public class UserStatsDto
{
    public int TotalRequests { get; set; }
    public int ApprovedRequests { get; set; }
    public int PendingRequests { get; set; }
    public int AvailableRequests { get; set; }
    public DateTime? LastRequestDate { get; set; }
    public int MovieRequests { get; set; }
    public int TvRequests { get; set; }
    public int MusicRequests { get; set; }
}

public class PlexAuthRequest
{
    [Required]
    public string PlexToken { get; set; } = string.Empty;
    [Required]
    public string PlexUsername { get; set; } = string.Empty;
}

public class AuthResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public UserDto User { get; set; } = new();
}

public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasPrevious => PageNumber > 1;
    public bool HasNext => PageNumber < TotalPages;
}

public class MediaFilterDto
{
    public string? SearchTerm { get; set; }
    public MediaType? MediaType { get; set; }
    public List<string> Genres { get; set; } = new();
    public int? YearFrom { get; set; }
    public int? YearTo { get; set; }
    public decimal? MinRating { get; set; }
    public Quality? Quality { get; set; }
    public bool? OnlyAvailable { get; set; }
    public bool? OnlyRequested { get; set; }
    public SortOrder SortBy { get; set; } = SortOrder.DateDescending;
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class ApiResponse<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string? Message { get; set; }
    public List<string> Errors { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class MediaRequestResult
{
    public bool Success { get; set; }
    public int? RequestId { get; set; }
    public string? ErrorMessage { get; set; }
    public RequestStatus? NewStatus { get; set; }
}

/// <summary>
/// Admin-configurable settings for how the downloader organizes finished downloads into the Plex
/// library: where things go (per-media-type root, plus optional routing rules for e.g. a separate
/// 4K/anime library), what they're named (token templates), and how the transfer happens.
/// Served to the downloader over the secured /api/fulfillment/library-config endpoint, mirroring
/// <see cref="DownloadPreferencesDto"/>'s hot-reloadable admin-config pattern.
/// </summary>
public class LibraryOrganizationPreferencesDto
{
    public string MoviePath { get; set; } = string.Empty;
    public string TvPath { get; set; } = string.Empty;

    /// <summary>Token template for a movie's destination path, e.g. "{Title} ({Year})/{Title} ({Year}){Ext}".</summary>
    public string MovieTemplate { get; set; } = "{Title} ({Year})/{Title} ({Year}){Ext}";
    /// <summary>Token template for a single TV episode's destination path.</summary>
    public string TvEpisodeTemplate { get; set; } = "{ShowTitle} ({Year})/Season {Season:00}/{ShowTitle} - s{Season:00}e{Episode:00} - {EpisodeTitle}{Ext}";
    /// <summary>Folder template used for a season pack when SplitSeasonPacks is off.</summary>
    public string SeasonPackFolderTemplate { get; set; } = "{ShowTitle} ({Year})/Season {Season:00}";

    /// <summary>Ordered routing rules (e.g. send Anime or a quality tier to a separate library root).
    /// First matching rule wins; no match falls back to MoviePath/TvPath + the default template.</summary>
    public List<LibraryRootRuleDto> LibraryRootRules { get; set; } = new();

    public TransferMode TransferMode { get; set; } = TransferMode.Hardlink;
    public bool ExtractArchives { get; set; } = true;
    public bool SplitSeasonPacks { get; set; } = true;
    public bool KeepSubtitles { get; set; } = true;
    public string SubtitleExtensionsCsv { get; set; } = ".srt,.ass,.ssa,.sub,.vtt";
    public string VideoExtensionsCsv { get; set; } = ".mkv,.mp4,.avi,.m4v,.ts,.mov,.wmv,.m2ts";
    /// <summary>Files smaller than this are treated as samples/junk rather than real episodes/movies.</summary>
    public double MinVideoFileSizeMb { get; set; } = 50;
    /// <summary>Delete the source after import instead of leaving it for the torrent client to keep seeding.
    /// Forced off when TransferMode is Hardlink (the "source" and the library copy are the same inode).</summary>
    public bool DeleteSourceAfterImport { get; set; } = false;
}

/// <summary>One routing rule: media type (+ optional quality/genre condition) -> an alternate library root
/// and, optionally, an alternate naming template.</summary>
public class LibraryRootRuleDto
{
    /// <summary>Movie or TvShow — the only values a job is ever actually tagged with.</summary>
    public MediaType MediaType { get; set; }
    public Quality? MinQuality { get; set; }
    /// <summary>Tri-state: null = don't care, true = anime only, false = non-anime only. Backed by the
    /// shared Animation+Japanese-origin heuristic (<see cref="PlexRequestsHosted.Shared.AnimeClassifier"/>),
    /// snapshotted on the job at enqueue time — not a raw genre-name match, since TMDB has no genre
    /// literally called "Anime" (Japanese and Western animation are both just "Animation").</summary>
    public bool? RequireAnime { get; set; }
    /// <summary>Simple substring match against the request's genre list, e.g. "Documentary". Null = no genre condition.</summary>
    public string? GenreContains { get; set; }
    public string RootPath { get; set; } = string.Empty;
    /// <summary>Null = use the media type's default template (MovieTemplate/TvEpisodeTemplate).</summary>
    public string? TemplateOverride { get; set; }
}

/// <summary>One file placed into the library by the organizer — the durable audit trail for a fulfillment job.</summary>
public class ImportedFileDto
{
    public string? TorrentId { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    /// <summary>"video" | "subtitle".</summary>
    public string FileType { get; set; } = "video";
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public long SizeBytes { get; set; }
}
