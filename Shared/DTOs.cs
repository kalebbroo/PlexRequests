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
public record ProgressRequest(int Progress, string? WorkerId, List<DownloadTorrentTelemetry>? Torrents = null);
public record FailRequest(string? Reason);

/// <summary>
/// Live, per-torrent download telemetry the downloader worker samples from the download client each
/// monitor tick and pushes up with its progress report. Ephemeral — the web app holds only the latest
/// snapshot in memory to drive the admin live-downloads panel; it is never persisted.
/// </summary>
public class DownloadTorrentTelemetry
{
    /// <summary>Torrent display name (from the release/magnet), for the admin row label.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Where this torrent is in its lifecycle: Downloading, Finishing, Importing, or Imported.</summary>
    public DownloadTorrentStage Stage { get; set; } = DownloadTorrentStage.Downloading;
    /// <summary>0-100 completion for this torrent.</summary>
    public double ProgressPercent { get; set; }
    /// <summary>Current download rate in bytes/sec (0 once finished/seeding).</summary>
    public double DownloadRateBytesPerSec { get; set; }
    public int Seeds { get; set; }
    public int Peers { get; set; }
    /// <summary>Estimated seconds to completion as reported by the client, if known (0/absent ⇒ unknown).</summary>
    public long? EtaSeconds { get; set; }
    public long TotalSizeBytes { get; set; }
    /// <summary>Season/episode this torrent covers, when the job is a TV fan-out (null for movies/whole packs).</summary>
    public int? Season { get; set; }
    public int? Episode { get; set; }
}

/// <summary>Per-torrent lifecycle stage surfaced in the admin live-downloads panel.</summary>
public enum DownloadTorrentStage
{
    /// <summary>Actively pulling bytes.</summary>
    Downloading = 0,
    /// <summary>Reported finished by the client; in the grace window before the files are resolvable on disk.</summary>
    Finishing = 1,
    /// <summary>Files resolved; being renamed and moved/hardlinked into the Plex library.</summary>
    Importing = 2,
    /// <summary>Imported into the library; the torrent is being (or has been) removed and kept seeding.</summary>
    Imported = 3
}

/// <summary>
/// A download job as rendered in the admin live-downloads panel: the request identity, its overall
/// lifecycle stage, and — while in-flight — the live per-torrent telemetry. Assembled by the web app
/// from the persisted <c>FulfillmentJob</c> row joined with the latest in-memory telemetry snapshot.
/// </summary>
public class DownloadJobView
{
    public int JobId { get; set; }
    public int MediaRequestId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public MediaType MediaType { get; set; }
    public string? PosterUrl { get; set; }
    public string? RequestedBy { get; set; }
    public FulfillmentStatus Status { get; set; }
    /// <summary>0-100 aggregate progress persisted for the job.</summary>
    public int Progress { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime? UpdatedAt { get; set; }
    /// <summary>Whether this is an in-flight job (true) or part of the recently-finished tail (false).</summary>
    public bool IsActive { get; set; }
    /// <summary>Human lifecycle label for the whole job (e.g. "Approved — queued", "Downloading", "Available").</summary>
    public string Stage { get; set; } = string.Empty;
    public List<DownloadTorrentTelemetry> Torrents { get; set; } = new();
}
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

// ---------------------------------------------------------------------------------------------
// Network shares (NAS / network drives). Admins add an SMB or NFS share in the admin UI; both the
// web app (read-only, for the folder browser) and the downloader (read-write, for placing files)
// mount it at the SAME path — /mnt/nas/{MountSlug} — so paths configured in Library Organization
// are valid in both containers. Passwords are encrypted at rest and never returned to the browser.
// ---------------------------------------------------------------------------------------------

/// <summary>A configured network share as shown in the admin UI. The password is never included;
/// <see cref="HasPassword"/> just signals whether one is stored.</summary>
public class NetworkShareDto
{
    public int Id { get; set; }
    /// <summary>Friendly name the admin gives the share (e.g. "Media NAS").</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Stable path-safe slug; the share is mounted at /mnt/nas/{MountSlug} in both containers.</summary>
    public string MountSlug { get; set; } = string.Empty;
    public NetworkShareProtocol Protocol { get; set; } = NetworkShareProtocol.Smb;
    /// <summary>NAS IP or hostname. IP is recommended (no DNS lookup, no chance of a hostname leak).</summary>
    public string Server { get; set; } = string.Empty;
    /// <summary>SMB share name, or the NFS export path (e.g. "media" / "/volume1/media").</summary>
    public string ShareName { get; set; } = string.Empty;
    /// <summary>Optional SMB domain/workgroup.</summary>
    public string? Domain { get; set; }
    /// <summary>SMB username (blank = guest/anonymous). Unused for NFS.</summary>
    public string? Username { get; set; }
    public bool HasPassword { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>The in-container mount path, /mnt/nas/{MountSlug}. Point Library Organization paths here.</summary>
    public string MountPath => string.IsNullOrEmpty(MountSlug) ? string.Empty : $"/mnt/nas/{MountSlug}";
    /// <summary>True when Server is a private/RFC1918 LAN address — the safe case (traffic stays local).</summary>
    public bool ServerIsPrivate { get; set; } = true;

    /// <summary>Live mount status from the web container (populated by the mount service, not stored).</summary>
    public NetworkMountStatusDto? Status { get; set; }
}

/// <summary>Create/update payload. A null <see cref="Password"/> on update keeps the existing password;
/// an empty string clears it (guest). Write-only — the password is never read back out.</summary>
public class NetworkShareEditDto
{
    public string Name { get; set; } = string.Empty;
    public NetworkShareProtocol Protocol { get; set; } = NetworkShareProtocol.Smb;
    public string Server { get; set; } = string.Empty;
    public string ShareName { get; set; } = string.Empty;
    public string? Domain { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Runtime mount state for one share, surfaced to the admin UI.</summary>
public class NetworkMountStatusDto
{
    public bool Mounted { get; set; }
    /// <summary>Human-readable error (mount stderr, trimmed) when the last mount attempt failed.</summary>
    public string? Error { get; set; }
    public DateTime CheckedAt { get; set; }
}

/// <summary>Full mount config INCLUDING the decrypted password. Server-to-downloader only, over the
/// shared-secret fulfillment API on the internal Docker network — never sent to a browser.</summary>
public class NetworkShareMountDto
{
    public string MountSlug { get; set; } = string.Empty;
    public NetworkShareProtocol Protocol { get; set; } = NetworkShareProtocol.Smb;
    public string Server { get; set; } = string.Empty;
    public string ShareName { get; set; } = string.Empty;
    public string? Domain { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>One subfolder returned by the admin folder-browser endpoint.</summary>
public class FolderEntryDto
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
}

/// <summary>
/// Result of listing one directory level for the Library Organization admin folder browser —
/// current/parent path plus its immediate subfolders. Null <see cref="ParentPath"/> means we're at a
/// top-level root (a Windows drive, or "/" on Linux/Mac) and "Up" isn't available.
/// </summary>
public class FolderBrowseResultDto
{
    public string? CurrentPath { get; set; }
    public string? ParentPath { get; set; }
    public List<FolderEntryDto> Directories { get; set; } = new();
}
