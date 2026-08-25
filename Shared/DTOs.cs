using PlexRequestsHosted.Shared.Enums;
using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Media;
using System.Text.Json.Serialization;

namespace PlexRequestsHosted.Shared.DTOs;

public abstract class BaseDto
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

// Media/version quality of an item already on Plex (resolution, codec, size, bitrate).
// Populated from the Plex library scan; null when the item isn't on Plex or predates quality capture.
public class MediaQualityDto
{
    public string? Resolution { get; set; }   // "4K", "1080p", "720p", "SD"
    public string? VideoCodec { get; set; }    // "HEVC", "H.264"
    public string? AudioCodec { get; set; }    // "EAC3", "AAC", "DTS"
    public int? Bitrate { get; set; }          // kbps
    public long? FileSizeBytes { get; set; }
    public int VersionCount { get; set; } = 1; // number of file versions on Plex

    // Compact chip label, e.g. "4K HEVC · 8.2 GB" (or "4K HEVC · 8.2 GB · 2 versions").
    [JsonIgnore]
    public string Label
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Resolution)) parts.Add(Resolution!);
            if (!string.IsNullOrEmpty(VideoCodec)) parts.Add(VideoCodec!);
            var head = parts.Count > 0 ? string.Join(' ', parts) : "On Plex";
            var tail = new List<string>();
            if (FileSizeBytes is > 0) tail.Add(FormatSize(FileSizeBytes.Value));
            if (VersionCount > 1) tail.Add($"{VersionCount} versions");
            return tail.Count > 0 ? $"{head} · {string.Join(" · ", tail)}" : head;
        }
    }

    // Full hover/expand detail, e.g. "4K · HEVC · EAC3 · 18.5 Mbps · 8.2 GB · 2 versions".
    [JsonIgnore]
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Resolution)) parts.Add(Resolution!);
            if (!string.IsNullOrEmpty(VideoCodec)) parts.Add(VideoCodec!);
            if (!string.IsNullOrEmpty(AudioCodec)) parts.Add(AudioCodec!);
            if (Bitrate is > 0) parts.Add($"{(Bitrate.Value / 1000.0):0.0} Mbps");
            if (FileSizeBytes is > 0) parts.Add(FormatSize(FileSizeBytes.Value));
            if (VersionCount > 1) parts.Add($"{VersionCount} versions");
            return parts.Count > 0 ? string.Join(" · ", parts) : "On Plex";
        }
    }

    private static string FormatSize(long bytes)
    {
        double gb = bytes / 1_000_000_000.0;
        if (gb >= 1) return $"{gb:0.0} GB";
        double mb = bytes / 1_000_000.0;
        return $"{mb:0} MB";
    }
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
    // Detailed Plex media quality (resolution/codec/size/versions); null when not on Plex.
    public MediaQualityDto? MediaQuality { get; set; }
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
    /// <summary>Compact secondary label, such as an album's artist or an artist's disambiguation.</summary>
    public string? Subtitle { get; set; }

    /// <summary>Canonical provider-neutral identity. Older servers/clients may leave this null.</summary>
    public MediaRef? MediaRef { get; set; }

    public MediaRef ResolveMediaRef()
    {
        if (MediaRef is { IsValid: true }) return MediaRef;
        if (!string.IsNullOrWhiteSpace(ExternalId))
            return PlexRequestsHosted.Shared.Media.MediaRef.FromExternal(ExternalSource ?? "external", ExternalId, MediaType,
                MediaType.DefaultKind());
        return PlexRequestsHosted.Shared.Media.MediaRef.FromTmdb(TmdbId ?? Id, MediaType);
    }
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

    // The next/last episode TMDB already returns in the base /tv/{id} body. These were being discarded,
    // which is a shame: knowing which season the next episode is in lets the calendar refresh only that
    // season instead of walking every one, and that's what makes a frequent refresh affordable.
    public DateTime? NextEpisodeAirDate { get; set; }
    public int? NextEpisodeSeason { get; set; }
    public int? NextEpisodeNumber { get; set; }
    public DateTime? LastEpisodeAirDate { get; set; }
    public string? TrailerUrl { get; set; }
    public List<string> Languages { get; set; } = new();
    public List<string> Countries { get; set; } = new();
    public MusicMetadataDto? Music { get; set; }
}

public sealed class MusicMetadataDto
{
    public MediaKind Kind { get; set; }
    public string? ArtistId { get; set; }
    public string? ArtistCredit { get; set; }
    public string? PrimaryType { get; set; }
    public List<string> SecondaryTypes { get; set; } = new();
    public string? ReleaseGroupId { get; set; }
    public string? ReleaseId { get; set; }
    /// <summary>Album containing a track result. Album detail rows continue to use the parent
    /// <see cref="MediaDetailDto.Title"/>; this prevents a requested song from being filed under Singles.</summary>
    public string? AlbumTitle { get; set; }
    public int TrackCount { get; set; }
    public List<MusicTrackMetadataDto> Tracks { get; set; } = new();
    /// <summary>Requestable album cards for an artist detail page. This retains the provider identities
    /// and artwork already returned by the catalog instead of reducing the discography to plain text.</summary>
    public List<MediaCardDto> Albums { get; set; } = new();
    public List<string> AlbumTitles { get; set; } = new();
}

/// <summary>
/// The small, immutable slice of music metadata needed to search and rank a request after it has crossed
/// into the downloader process. Keeping this separate from the full provider response makes durable jobs
/// stable even when MusicBrainz is unavailable or its response shape changes between retries.
/// </summary>
public sealed class MusicAcquisitionContextDto
{
    /// <summary>Durable payload shape. Jobs created before enrichment support deserialize as v1 and are
    /// refreshed once when claimed; current jobs freeze album identity and artwork as v2.</summary>
    public int SchemaVersion { get; set; } = 1;
    public MediaKind Kind { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Track { get; set; }
    /// <summary>Provider artwork frozen with the job so acquisition never has to re-query metadata.</summary>
    public string? ArtworkUrl { get; set; }
    public int TrackCount { get; set; }
    /// <summary>Immutable track order captured from metadata when the job is enqueued. The downloader uses
    /// this to name an album deterministically without making provider calls from the VPN container.</summary>
    public List<MusicTrackMetadataDto> Tracks { get; set; } = new();
    /// <summary>Artist-catalog completion contract. Plex must contain every album MusicBrainz reported
    /// when the durable job was created; merely finding a pre-existing partial artist is not success.</summary>
    public List<string> ExpectedAlbums { get; set; } = new();
    /// <summary>Optional provider-specific stream identities resolved by the web app before the job crosses
    /// the VPN boundary. The canonical request identity and Plex completion contract remain unchanged.</summary>
    public DirectAudioAcquisitionContextDto? DirectAudio { get; set; }

    /// <summary>True only when the durable job contains enough immutable metadata to prove completion after
    /// import. Acquisition must wait when this is false; otherwise a partial provider response can create a
    /// download that can never be verified and will be retried forever.</summary>
    [JsonIgnore]
    public bool HasCompletionContract => Kind switch
    {
        MediaKind.Artist => !string.IsNullOrWhiteSpace(Artist)
            && ExpectedAlbums is { Count: > 0 } albums
            && albums.All(x => !string.IsNullOrWhiteSpace(x)),
        MediaKind.Album => !string.IsNullOrWhiteSpace(Artist) && !string.IsNullOrWhiteSpace(Album)
            && Tracks is { Count: > 0 } albumTracks
            && (TrackCount <= 0 || albumTracks.Count >= TrackCount)
            && albumTracks.All(x => !string.IsNullOrWhiteSpace(x.Title)),
        MediaKind.Track => !string.IsNullOrWhiteSpace(Artist) && !string.IsNullOrWhiteSpace(Track)
            && Tracks is { Count: > 0 } trackMetadata
            && trackMetadata.All(x => !string.IsNullOrWhiteSpace(x.Title)),
        _ => false
    };

    public string BuildSearchText(string fallbackTitle) => Kind switch
    {
        MediaKind.Artist => Join(Artist ?? fallbackTitle, "discography"),
        MediaKind.Track => Join(Artist, Track ?? fallbackTitle),
        _ => Join(Artist, Album ?? fallbackTitle)
    };

    private static string Join(params string?[] values) => string.Join(' ', values
        .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));
}

/// <summary>A complete, immutable direct-audio manifest. Keeping this separate from the canonical music
/// metadata lets any supported catalog use an optional acquisition provider without pretending the two
/// providers share identifiers.</summary>
public sealed class DirectAudioAcquisitionContextDto
{
    public string Provider { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public List<MusicTrackMetadataDto> Tracks { get; set; } = new();
}

public sealed class MusicTrackMetadataDto
{
    public string RecordingId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? ArtistCredit { get; set; }
    public int DiscNumber { get; set; }
    public int TrackNumber { get; set; }
    public int? DurationMs { get; set; }
}

public sealed class MusicSettingsDto
{
    /// <summary>Show music metadata results and discovery rows.</summary>
    public bool CatalogEnabled { get; set; }
    /// <summary>Stable key of the catalog used for new music searches. Existing item identities continue
    /// to resolve through the provider that created them.</summary>
    public string MetadataProvider { get; set; } = "musicbrainz";
    /// <summary>Allow music requests to enter automatic fulfillment. Kept separate from catalog visibility
    /// so an admin can browse/test metadata before a writable music library has been configured.</summary>
    public bool RequestsEnabled { get; set; }
    /// <summary>Offer YouTube Music's own audio as an acquisition source for YouTube-backed track and
    /// album requests. This is independent from torrent indexers and can be disabled without hiding the catalog.</summary>
    public bool DirectDownloadsEnabled { get; set; }
    /// <summary>Concrete configuration gaps that currently prevent safe automatic fulfillment.</summary>
    public List<string> ReadinessIssues { get; set; } = new();
    public DateTime? UpdatedAt { get; set; }
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
    // Dominant/best quality across this season's episodes on Plex; null when not on Plex.
    public MediaQualityDto? MediaQuality { get; set; }
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
    // Quality of this episode's file on Plex; null when not on Plex.
    public MediaQualityDto? MediaQuality { get; set; }
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
    /// <summary>
    /// The quality profile the requester chose, when they were offered a choice. Null means "decide for me"
    /// — the assignment rules and then the default profile apply. This replaces a <c>PreferredQuality</c>
    /// field that was declared but never persisted or read, so the quality a user picked genuinely had no
    /// effect on anything.
    /// </summary>
    public int? QualityProfileId { get; set; }
    /// <summary>Display name of the resolved profile, for the requests list.</summary>
    public string? QualityProfileName { get; set; }
    public string? RequestNote { get; set; }
    public string? DenialReason { get; set; }
    public List<int> RequestedSeasons { get; set; } = new();
    public bool RequestAllSeasons { get; set; }
    public string? RequestedEpisodesCsv { get; set; }   // "S1E1,S2E5" — episode-level targets
    public bool Monitored { get; set; }                 // ongoing-series auto-download
    public string? ExternalId { get; set; }             // non-TMDb id (music MBID / Plex ratingKey)
    public string? ExternalSource { get; set; }
    public MediaRef? MediaRef { get; set; }
    public RequestScopeKind RequestScopeKind { get; set; }
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
    public bool ReplacementRequested { get; set; }
    public int? ReplacementJobId { get; set; }
    public FulfillmentStatus? ReplacementJobStatus { get; set; }
    public int ReplacementProgress { get; set; }
    public int ReplacementAttempts { get; set; }
    public DateTime? ReplacementNextRetryAt { get; set; }
    public string? ReplacementLastError { get; set; }
    public IssueStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>Structured problem report submitted from a media detail page.</summary>
public sealed class MediaIssueReportDto
{
    public int MediaId { get; set; }
    public MediaType MediaType { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? PosterUrl { get; set; }
    public string Reason { get; set; } = "Other";
    public string? Detail { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public bool RequestReplacement { get; set; }
}

public sealed class MediaIssueResultDto
{
    public bool Success { get; set; }
    public int? IssueId { get; set; }
    public bool ReplacementQueued { get; set; }
    public string? ErrorMessage { get; set; }
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
    /// <summary>On: the first search accepts only the job's Quality or better; if nothing has appeared by the
    /// next search pass the best available release is taken instead (and later auto-upgraded once the preferred
    /// quality exists). Off: the best available release is taken immediately (preferred quality still ranks first).</summary>
    public bool EnforceQualityFloor { get; set; } = true;

    /// <summary>
    /// Minimum normalized token-overlap similarity (0-1) between a release name and the requested title
    /// for the release to be considered at all. Guards free-text-search indexers (1337x/ext.to/Nyaa)
    /// against accepting an unrelated/wrong-show/spam release just because it clears quality/seeder/size
    /// thresholds.
    /// </summary>
    public double MinTitleSimilarity { get; set; } = 0.5;

    /// <summary>Empty searches to tolerate before relaxing the quality target. See the entity for why this
    /// isn't keyed off raw attempt count.</summary>
    public int RelaxFloorAfterEmptySearches { get; set; } = 3;

    /// <summary>When a user requests an entire series, automatically monitor it for new episodes.</summary>
    public bool AutoMonitorEntireSeriesRequests { get; set; } = true;

    /// <summary>Supplement live fulfillment searches with candidates from the durable release catalog.</summary>
    public bool UseCatalogForSearch { get; set; }

    /// <summary>Use the durable catalog as the primary source for wanted-episode monitoring.</summary>
    public bool UseCatalogForMonitoring { get; set; }
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

/// <summary>Discord connection details for the currently authenticated Plex Requests user.</summary>
public sealed class DiscordLinkStatusDto
{
    public bool Linked { get; set; }
    public string? DiscordUsername { get; set; }
    public bool DmOptIn { get; set; }
}

public class RequestQuotaDto
{
    public int? Limit { get; set; }
    public int WindowDays { get; set; } = 7;
    public int Used { get; set; }
    public int? Remaining => Limit is null ? null : Math.Max(0, Limit.Value - Used);
}

public class UserQuotaOverviewDto
{
    public RequestQuotaDto Movie { get; set; } = new();
    public RequestQuotaDto Tv { get; set; } = new();
    public RequestQuotaDto Music { get; set; } = new();
}

public class UserGroupDto : BaseDto
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public UserPermission Permissions { get; set; } = UserPermission.AllRequests;
    public int? MovieRequestLimit { get; set; } = 5;
    public int MovieRequestLimitDays { get; set; } = 7;
    public int? TvRequestLimit { get; set; } = 3;
    public int TvRequestLimitDays { get; set; } = 7;
    public int? MusicRequestLimit { get; set; } = 10;
    public int MusicRequestLimitDays { get; set; } = 7;
    public List<int> AllowedQualityProfileIds { get; set; } = new();
    public bool IsSystem { get; set; }
    public int MemberCount { get; set; }
}

public class AdminUserDto : UserDto
{
    public int? GroupId { get; set; }
    public string? GroupName { get; set; }
    public UserPermission CustomPermissions { get; set; }
    public UserPermission EffectivePermissions { get; set; }
    public List<int> CustomAllowedQualityProfileIds { get; set; } = new();
    public List<int> EffectiveAllowedQualityProfileIds { get; set; } = new();
    public int CustomMovieRequestLimitDays { get; set; } = 7;
    public int CustomTvRequestLimitDays { get; set; } = 7;
    public int CustomMusicRequestLimitDays { get; set; } = 7;
    public DateTime? LastLoginAt { get; set; }
    public DateTime ProfileCreatedAt { get; set; }
    public RequestQuotaDto MovieQuota { get; set; } = new();
    public RequestQuotaDto TvQuota { get; set; } = new();
    public RequestQuotaDto MusicQuota { get; set; } = new();
    public UserAccountStatus AccountStatus { get; set; } = UserAccountStatus.Active;
    public DateTime? SuspendedUntil { get; set; }
    public string? AccountStatusReason { get; set; }
    public DateTime? AccountStatusChangedAt { get; set; }
    public string? AccountStatusChangedBy { get; set; }
}

/// <summary>Read-only administrator view of one user's request and issue history.</summary>
public sealed class AdminUserActivityDto
{
    public int UserId { get; set; }
    public UserQuotaOverviewDto Quotas { get; set; } = new();
    public int TotalRequests { get; set; }
    public int ActiveRequests { get; set; }
    public int AvailableRequests { get; set; }
    public int FailedRequests { get; set; }
    public int MovieRequests { get; set; }
    public int TvRequests { get; set; }
    public int MusicRequests { get; set; }
    public int TotalIssues { get; set; }
    public int OpenIssues { get; set; }
    public int ReplacementIssues { get; set; }
    public int ResolvedIssues { get; set; }
    public bool DiscordLinked { get; set; }
    public string? DiscordUsername { get; set; }
    public bool DiscordDmOptIn { get; set; }
    public List<AdminUserRequestActivityDto> RecentRequests { get; set; } = new();
    public List<AdminUserRequestActivityDto> QuotaContributors { get; set; } = new();
    public bool QuotaContributorsTruncated { get; set; }
    public List<AdminUserIssueActivityDto> RecentIssues { get; set; } = new();
}

public sealed class AdminUserRequestActivityDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? PosterUrl { get; set; }
    public MediaType MediaType { get; set; }
    public RequestScopeKind Scope { get; set; }
    public RequestStatus Status { get; set; }
    public DateTime RequestedAt { get; set; }
    public bool CountsTowardQuota { get; set; }
    public DateTime? QuotaExpiresAt { get; set; }
    public bool Monitored { get; set; }
    public string? RequestedSeasonsCsv { get; set; }
    public string? RequestedEpisodesCsv { get; set; }
}

public sealed class AdminUserIssueActivityDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? PosterUrl { get; set; }
    public MediaType MediaType { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public IssueStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public class UserAccessEditDto
{
    public int UserId { get; set; }
    public int? GroupId { get; set; }
    public UserPermission CustomPermissions { get; set; } = UserPermission.AllRequests;
    public int? MovieRequestLimit { get; set; } = 5;
    public int MovieRequestLimitDays { get; set; } = 7;
    public int? TvRequestLimit { get; set; } = 3;
    public int TvRequestLimitDays { get; set; } = 7;
    public int? MusicRequestLimit { get; set; } = 10;
    public int MusicRequestLimitDays { get; set; } = 7;
    public List<int> CustomAllowedQualityProfileIds { get; set; } = new();
    public UserAccountStatus AccountStatus { get; set; } = UserAccountStatus.Active;
    public DateTime? SuspendedUntil { get; set; }
    public string? AccountStatusReason { get; set; }
}

public record UserAdminResult(bool Success, string Message)
{
    public static UserAdminResult Ok(string message) => new(true, message);
    public static UserAdminResult Fail(string message) => new(false, message);
}

public sealed class UserAccessAuditDto
{
    public long Id { get; set; }
    public UserAccessAuditTarget TargetType { get; set; }
    public int TargetId { get; set; }
    public string TargetName { get; set; } = string.Empty;
    public UserAccessAuditAction Action { get; set; }
    public int? ActorUserId { get; set; }
    public string ActorUsername { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public sealed class UserAccessAuditPageDto
{
    public List<UserAccessAuditDto> Items { get; set; } = new();
    public bool HasMore { get; set; }
    public long? NextCursor { get; set; }
}

// Wire types for the fulfillment worker API — shared by the web app (endpoints) and the downloader (client).
public record ClaimRequest(string? WorkerId, int? Max);

public record ProgressRequest(int Progress, string? WorkerId, List<DownloadTransferTelemetry>? Transfers = null);
/// <param name="CandidatesRejected">
/// True when the search DID return releases but none were acceptable. That is the only situation in which
/// holding out for the preferred quality is costing anything, so it's the counter that decides when to
/// relax the quality floor — as opposed to "the title isn't out yet", where relaxing would achieve nothing.
/// </param>
public record FailRequest(string? Reason, bool CandidatesRejected = false);

/// <summary>
/// Live, per-transfer telemetry the downloader worker samples from the active backend each
/// monitor tick and pushes up with its progress report. Ephemeral — the web app holds only the latest
/// snapshot in memory to drive the admin live-downloads panel; it is never persisted.
/// </summary>
public class DownloadTransferTelemetry
{
    /// <summary>Transfer display name, for the admin row label.</summary>
    public string Name { get; set; } = string.Empty;
    public AcquisitionProtocol Protocol { get; set; } = AcquisitionProtocol.Torrent;
    /// <summary>Indexer/provider that supplied the selected release, when known.</summary>
    public string? Source { get; set; }
    public int? IndexerId { get; set; }
    /// <summary>Where this torrent is in its lifecycle: Downloading, Finishing, Importing, or Imported.</summary>
    public DownloadTransferStage Stage { get; set; } = DownloadTransferStage.Downloading;
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

/// <summary>
/// One transfer backing a job, as the database durably knows it. Distinct from
/// <see cref="DownloadTransferTelemetry"/>, which is an in-memory snapshot for the live panel and is lost
/// whenever the worker restarts — this is the record that survives, and the join key the reconciler uses.
/// </summary>
public class TrackedTransferDto
{
    public int Id { get; set; }
    public int FulfillmentJobId { get; set; }
    /// <summary>The acquisition backend's own id. Never computed by us.</summary>
    public string TransferId { get; set; } = string.Empty;
    public AcquisitionProtocol Protocol { get; set; } = AcquisitionProtocol.Torrent;
    /// <summary>Stable provider identity for deduplication/blocklisting (an info hash for torrents).</summary>
    public string? SourceId { get; set; }
    public string? ReleaseName { get; set; }
    public string? Source { get; set; }
    public int? IndexerId { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public bool IsPack { get; set; }
    public List<int> NeededEpisodes { get; set; } = new();
    public int Resolution { get; set; }

    public TransferTrackingState State { get; set; }
    public double Progress { get; set; }
    public int Seeds { get; set; }
    public int Peers { get; set; }
    public double DownloadRateBytesPerSec { get; set; }
    public long TotalSizeBytes { get; set; }
    public DateTime AddedAt { get; set; }
    public DateTime? ProgressChangedAt { get; set; }
    public string? TrackerStatus { get; set; }
    public string? FailReason { get; set; }
}

/// <summary>One transfer's outcome from a reconciliation pass.</summary>
public class TransferStateUpdateDto
{
    public string TransferId { get; set; } = string.Empty;
    public AcquisitionProtocol Protocol { get; set; } = AcquisitionProtocol.Torrent;
    public TransferTrackingState State { get; set; }
    public double Progress { get; set; }
    public int Seeds { get; set; }
    public int Peers { get; set; }
    public double DownloadRateBytesPerSec { get; set; }
    public long TotalSizeBytes { get; set; }
    public string? TrackerStatus { get; set; }
    /// <summary>Why it failed or went missing; null while healthy.</summary>
    public string? Reason { get; set; }
}

/// <summary>Per-transfer lifecycle stage surfaced in the admin live-downloads panel.</summary>
public enum DownloadTransferStage
{
    /// <summary>Actively pulling bytes.</summary>
    Downloading = 0,
    /// <summary>Reported finished by the client; in the grace window before the files are resolvable on disk.</summary>
    Finishing = 1,
    /// <summary>Files resolved; being renamed and moved/hardlinked into the Plex library.</summary>
    Importing = 2,
    /// <summary>Imported into the library; the torrent is being (or has been) removed and kept seeding.</summary>
    Imported = 3,
    /// <summary>The client or import pipeline rejected this release.</summary>
    Failed = 4,
    /// <summary>The persisted torrent is no longer present in the download client.</summary>
    Missing = 5
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
    public List<DownloadTransferTelemetry> Transfers { get; set; } = new();
}
public record RefreshLibraryRequest(MediaType MediaType);

public sealed record PlexVerificationRequest(
    MediaType MediaType,
    MediaKind Kind,
    string? ExternalSource,
    string? ExternalId,
    string? Artist,
    string? Album,
    string? Track,
    List<string>? ExpectedAlbums = null,
    List<string>? ExpectedTracks = null,
    int? ExpectedDurationMs = null);

public sealed record PlexVerificationResult(bool Available, string? RatingKey = null, string? Detail = null);

public class FulfillmentJobDto
{
    public int Id { get; set; }
    public int MediaRequestId { get; set; }
    public int MediaId { get; set; }
    public MediaType MediaType { get; set; }
    public MediaRef? Media { get; set; }
    public RequestScopeKind RequestScope { get; set; }
    public MusicAcquisitionContextDto? Music { get; set; }
    /// <summary>Protocols the admin currently permits for this job. Torrent is always present for backward
    /// compatibility; optional modules add their own protocol when configured and ready.</summary>
    public List<AcquisitionProtocol> AllowedAcquisitionProtocols { get; set; } = [AcquisitionProtocol.Torrent];
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

    /// <summary>
    /// The full quality profile governing this job, embedded rather than referenced by id: the downloader
    /// must be able to rank without a second round-trip mid-search, and embedding it also freezes the
    /// profile for the life of the job so an admin edit can't change what an in-flight search is hunting.
    /// Null for a job enqueued before profiles existed — the flat <see cref="Quality"/> floor applies then.
    /// </summary>
    public QualityProfileDto? QualityProfile { get; set; }

    /// <summary>The quality-tier catalog, needed to resolve a release's (resolution, source) to a tier.</summary>
    public List<QualityDefinitionDto> QualityDefinitions { get; set; } = new();

    /// <summary>Enabled custom formats, each carrying its score in THIS job's profile.</summary>
    public List<CustomFormatDto> CustomFormats { get; set; } = new();

    /// <summary>
    /// Searches that returned candidates but nothing acceptable. Drives when the quality floor is relaxed —
    /// deliberately not <see cref="Attempts"/>, which counts every claim including re-searches, so using it
    /// meant a single deferral dropped the floor permanently.
    /// </summary>
    public int EmptySearchCount { get; set; }

    /// <summary>Legacy torrent hashes and protocol-qualified source ids that already failed for this
    /// request. Delivered with the job so a search needs no extra round-trip to find out.</summary>
    public List<string> BlocklistedHashes { get; set; } = new();

    /// <summary>An admin chose this exact release. The downloader skips searching and ranking entirely —
    /// forcing a grab is an override of that judgement, so re-deriving it would defeat the point.</summary>
    public bool IsManualGrab { get; set; }
    public string? ForcedMagnet { get; set; }
    public string? ForcedReleaseName { get; set; }
    public int? ForcedIndexerId { get; set; }

    /// <summary>Genres snapshotted at enqueue time (for admin-configured library-routing rules).</summary>
    public List<string> Genres { get; set; } = new();
    /// <summary>Animation+Japanese-origin heuristic result, snapshotted at enqueue time (see <see cref="PlexRequestsHosted.Shared.AnimeClassifier"/>).</summary>
    public bool IsAnime { get; set; }
    public FulfillmentStatus Status { get; set; }
    public int Attempts { get; set; }
    public int Progress { get; set; }
    /// <summary>True when this is an automatic quality-upgrade job: the downloader enforces the quality floor
    /// unconditionally (never a downgrade) and, on success, replaces <see cref="ReplacePaths"/> instead of
    /// treating the content as newly acquired.</summary>
    public bool IsUpgrade { get; set; }
    /// <summary>Issue-triggered safe replacement. These jobs retry instead of ending after one empty search.</summary>
    public bool IsReplacement { get; set; }
    public int? MediaIssueId { get; set; }
    /// <summary>For an upgrade job: existing library file paths this upgrade supersedes. The downloader
    /// deletes any of these NOT re-written by the new import; the web app clears their audit rows.</summary>
    public List<string> ReplacePaths { get; set; } = new();
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

/// <summary>One quality tier in the catalog: a (resolution, source) pair such as "WEBDL-1080p".</summary>
public class QualityDefinitionDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Resolution { get; set; }
    public ReleaseSource Source { get; set; }
    /// <summary>Total ordering across the catalog; higher is better.</summary>
    public int SortWeight { get; set; }
}

/// <summary>
/// One entry in a profile's ordered tier list — either a single quality definition or a named group of
/// definitions treated as equivalent. Index in the list is the rank, ascending from worst.
/// </summary>
public class QualityProfileItemDto
{
    /// <summary>Stable key: "q:{definitionId}" for a single tier, "g:{slug}" for a group.</summary>
    public string K { get; set; } = string.Empty;
    /// <summary>Display name.</summary>
    public string N { get; set; } = string.Empty;
    /// <summary>Whether releases matching this tier may be grabbed at all.</summary>
    public bool Allowed { get; set; }
    /// <summary>Definition ids in this group; null for a single-tier entry.</summary>
    public int[]? Members { get; set; }
}

/// <summary>A named quality target: which tiers are acceptable, in what order, and where to stop upgrading.</summary>
public class QualityProfileDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsUserSelectable { get; set; } = true;
    public int AppliesToMediaTypes { get; set; }
    public int SortOrder { get; set; }
    public List<QualityProfileItemDto> Items { get; set; } = new();
    public int CutoffQualityDefinitionId { get; set; }
    public bool UpgradeAllowed { get; set; } = true;
    public int MinCustomFormatScore { get; set; }
    public int CutoffFormatScore { get; set; }
    public double? MinSizeGb { get; set; }
    public double? MaxSizeGb { get; set; }
    public double? MaxSeasonPackSizeGb { get; set; }
    public int? MinSeeders { get; set; }
    /// <summary>Comma-separated language codes a release must offer. Null/empty = any.</summary>
    public string? AllowedLanguagesCsv { get; set; }
    /// <summary>True when a request or assignment rule references this profile, so deletion is blocked.</summary>
    public bool InUse { get; set; }
}

/// <summary>Id + name only, for the requester's profile picker.</summary>
public class QualityProfileSummaryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
}

/// <summary>Condition-based assignment of a quality profile to a title.</summary>
public class ProfileAssignmentRuleDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool Enabled { get; set; }
    public MediaType? MatchMediaType { get; set; }
    public string? MatchGenre { get; set; }
    public int? MatchTmdbId { get; set; }
    public string? MatchLibrary { get; set; }
    public int QualityProfileId { get; set; }
    public string QualityProfileName { get; set; } = string.Empty;

    /// <summary>No condition set, so this rule would match every title. Enabling it is rejected.</summary>
    public bool IsUnconditional =>
        MatchMediaType is null && MatchTmdbId is null
        && string.IsNullOrWhiteSpace(MatchGenre) && string.IsNullOrWhiteSpace(MatchLibrary);
}

/// <summary>Admin view of one indexer provider: control fields + rolling health (see IndexerSettingEntity).</summary>
public class IndexerSettingDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Which code path searches this row ("Torznab", "EZTV", "1337x", ...).</summary>
    public string Implementation { get; set; } = string.Empty;
    /// <summary>Seeded by the app: disableable but not deletable.</summary>
    public bool IsBuiltIn { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 25;

    public string? Url { get; set; }
    /// <summary>Whether a key is stored. The key itself is never sent to the browser; an empty
    /// <see cref="ApiKey"/> on save means "leave the stored one alone".</summary>
    public bool HasApiKey { get; set; }
    /// <summary>Write-only: a new key being set. Null/blank leaves the existing one untouched.</summary>
    public string? ApiKey { get; set; }
    public string? BaseUrlOverride { get; set; }

    public string? MovieCategoriesCsv { get; set; }
    public string? TvCategoriesCsv { get; set; }
    public string? AnimeCategoriesCsv { get; set; }
    public bool SupportsMovie { get; set; } = true;
    public bool SupportsTv { get; set; } = true;
    public bool SupportsAnime { get; set; } = true;
    public bool AnimeOnly { get; set; }
    public List<IndexerMediaCapabilityDto> MediaCapabilities { get; set; } = new();

    public bool EnableIngestion { get; set; } = true;
    public bool EnableAutomaticSearch { get; set; } = true;
    public bool EnableInteractiveSearch { get; set; } = true;
    public bool EnableRecommendedFeed { get; set; }

    public int? MinSeeders { get; set; }
    public int? TimeoutSeconds { get; set; }
    public int RateLimitPerMinute { get; set; } = 30;
    public int CacheSeconds { get; set; } = 300;
    public int MaxDetailFetches { get; set; } = 25;
    public int? MaxAgeDays { get; set; }
    public string? Notes { get; set; }

    public DateTime? LastSearchAt { get; set; }
    public int LastResultCount { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    public long TotalSearches { get; set; }
    public long TotalResults { get; set; }
    public int LastLatencyMs { get; set; }

    /// <summary>Set while the indexer is refusing to answer. Distinct from an empty result, so the panel can
    /// say "Blocked by Cloudflare" and offer the remedy instead of showing a healthy source finding nothing.</summary>
    public IndexerBlockReason LastBlockReason { get; set; }
    public DateTime? LastBlockedAt { get; set; }
    public DateTime? SearchCircuitOpenUntil { get; set; }

    // --- Cloudflare clearance (write-only cookie, like the API key) --------------------------------
    /// <summary>Raw Cookie header to send, e.g. "cf_clearance=…". Never returned to the browser.</summary>
    public string? ClearanceCookie { get; set; }
    /// <summary>True when one is stored, so the UI can say so without revealing it.</summary>
    public bool HasClearance { get; set; }
    /// <summary>Explicitly wipe the stored clearance (blank means "leave alone", as with the API key).</summary>
    public bool ClearClearance { get; set; }
    public DateTime? ClearanceObtainedAt { get; set; }
    /// <summary>The User-Agent the clearance was earned with. Sent with it; useless apart from it.</summary>
    public string? UserAgent { get; set; }
}

/// <summary>
/// Full per-indexer definition served to the downloader (GET /api/fulfillment/indexers). Unlike the admin
/// DTO this carries the DECRYPTED API key — it crosses only the authenticated worker API, the same boundary
/// that already ships network-share credentials.
/// </summary>
public class IndexerConfigDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Implementation { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 25;

    public string? Url { get; set; }
    public string? ApiKey { get; set; }
    public string? BaseUrlOverride { get; set; }

    /// <summary>
    /// An optional clearance cookie supplied by an operator, as a raw Cookie header
    /// ("cf_clearance=…"), together with the User-Agent that earned it. Cloudflare binds the cookie to the
    /// exact UA *and* the client IP, so the two travel together and are useless apart — which is why this
    /// is one paired setting rather than two independent fields.
    /// </summary>
    public string? ClearanceCookie { get; set; }
    public string? UserAgent { get; set; }

    public string? MovieCategoriesCsv { get; set; }
    public string? TvCategoriesCsv { get; set; }
    public string? AnimeCategoriesCsv { get; set; }
    public bool SupportsMovie { get; set; } = true;
    public bool SupportsTv { get; set; } = true;
    public bool SupportsAnime { get; set; } = true;
    public bool AnimeOnly { get; set; }
    public List<IndexerMediaCapabilityDto> MediaCapabilities { get; set; } = new();

    public bool EnableIngestion { get; set; } = true;
    public bool EnableAutomaticSearch { get; set; } = true;
    public bool EnableInteractiveSearch { get; set; } = true;
    public bool EnableRecommendedFeed { get; set; }
    public DateTime? SearchCircuitOpenUntil { get; set; }

    public int? MinSeeders { get; set; }
    public int? TimeoutSeconds { get; set; }
    public int RateLimitPerMinute { get; set; } = 30;
    public int CacheSeconds { get; set; } = 300;
    public int MaxDetailFetches { get; set; } = 25;
    public int? MaxAgeDays { get; set; }

    /// <summary>Category ids for a media type, falling back to the Torznab standards when unset.</summary>
    public string CategoriesFor(MediaType mediaType) => mediaType switch
    {
        _ when MediaCapabilities.FirstOrDefault(x => x.MediaType == mediaType) is { } capability =>
            string.IsNullOrWhiteSpace(capability.CategoriesCsv) ? DefaultCategories(mediaType) : capability.CategoriesCsv,
        MediaType.Movie => string.IsNullOrWhiteSpace(MovieCategoriesCsv) ? "2000" : MovieCategoriesCsv,
        MediaType.Anime => string.IsNullOrWhiteSpace(AnimeCategoriesCsv) ? "5000,5070" : AnimeCategoriesCsv,
        MediaType.Music => "3000",
        _ => string.IsNullOrWhiteSpace(TvCategoriesCsv) ? "5000" : TvCategoriesCsv
    };

    public bool Supports(MediaType mediaType) => MediaCapabilities.Count > 0
        ? MediaCapabilities.Any(x => x.MediaType == mediaType && x.Enabled)
        : mediaType switch
        {
            MediaType.Movie => SupportsMovie,
            MediaType.TvShow => SupportsTv,
            MediaType.Anime => SupportsAnime,
            _ => false
        };

    private static string DefaultCategories(MediaType mediaType) => mediaType switch
    {
        MediaType.Movie => "2000",
        MediaType.TvShow => "5000",
        MediaType.Anime => "5000,5070",
        MediaType.Music => "3000",
        _ => string.Empty
    };
}

public class IndexerMediaCapabilityDto
{
    public MediaType MediaType { get; set; }
    public bool Enabled { get; set; }
    public string? CategoriesCsv { get; set; }
}

/// <summary>A Torznab endpoint the downloader found in its own environment, pushed once for import.</summary>
public class LegacyTorznabEndpointDto
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>One provider's outcome for one search, reported by the downloader
/// (POST /api/fulfillment/indexer-status) to drive the admin Indexers panel's health columns.</summary>
public class IndexerStatusReportDto
{
    /// <summary>Row id. Preferred over the name, which admins can now rename.</summary>
    public int IndexerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Success { get; set; }
    public int ResultCount { get; set; }
    public string? Error { get; set; }
    public int LatencyMs { get; set; }
    public DateTime SearchedAt { get; set; }
    /// <summary>Set when the indexer refused rather than answered — drives the panel's "Blocked" state.</summary>
    public IndexerBlockReason BlockReason { get; set; }
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
    /// <summary>Optional request-status filter for request history and the administrator approval queue.</summary>
    public RequestStatus? RequestStatus { get; set; }
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
    public bool JobQueued { get; set; }
    /// <summary>No new job was needed because the durable monitor child already has live work.</summary>
    public bool AlreadyCovered { get; set; }
    public int? RequestId { get; set; }
    public string? ErrorMessage { get; set; }
    public RequestStatus? NewStatus { get; set; }
    /// <summary>The durable scope that was accepted, allowing clients to describe the request accurately.</summary>
    public RequestScopeKind? RequestScope { get; set; }
    /// <summary>True when the accepted request will continue watching for future releases.</summary>
    public bool MonitorsFutureReleases { get; set; }
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
    public string MusicPath { get; set; } = string.Empty;

    /// <summary>Token template for a movie's destination path, e.g. "{Title} ({Year})/{Title} ({Year}){Ext}".</summary>
    public string MovieTemplate { get; set; } = "{Title} ({Year})/{Title} ({Year}){Ext}";
    /// <summary>Token template for a single TV episode's destination path.</summary>
    public string TvEpisodeTemplate { get; set; } = "{ShowTitle} ({Year})/Season {Season:00}/{ShowTitle} - s{Season:00}e{Episode:00} - {EpisodeTitle}{Ext}";
    /// <summary>Folder template used for a season pack when SplitSeasonPacks is off.</summary>
    public string SeasonPackFolderTemplate { get; set; } = "{ShowTitle} ({Year})/Season {Season:00}";
    /// <summary>Plex-friendly album layout. Artist-catalog requests retain their album subfolders below
    /// the artist root; track requests use the same tokens with the available context.</summary>
    public string MusicTrackTemplate { get; set; } = "{Artist}/{Album} ({Year})/{Disc:00}-{Track:00} - {TrackTitle}{Ext}";

    /// <summary>Ordered routing rules (e.g. send Anime or a quality tier to a separate library root).
    /// First matching rule wins; no match falls back to MoviePath/TvPath + the default template.</summary>
    public List<LibraryRootRuleDto> LibraryRootRules { get; set; } = new();

    public TransferMode TransferMode { get; set; } = TransferMode.Hardlink;
    public bool ExtractArchives { get; set; } = true;
    public bool SplitSeasonPacks { get; set; } = true;
    public bool KeepSubtitles { get; set; } = true;
    public string SubtitleExtensionsCsv { get; set; } = ".srt,.ass,.ssa,.sub,.vtt";
    public string VideoExtensionsCsv { get; set; } = ".mkv,.mp4,.avi,.m4v,.ts,.mov,.wmv,.m2ts";
    public string AudioExtensionsCsv { get; set; } = ".flac,.mp3,.m4a,.aac,.ogg,.opus,.wav,.wma,.alac";
    /// <summary>Files smaller than this are treated as samples/junk rather than real episodes/movies.</summary>
    public double MinVideoFileSizeMb { get; set; } = 50;
    public double MinAudioFileSizeMb { get; set; } = 1;
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
    public string? TransferId { get; set; }
    public AcquisitionProtocol Protocol { get; set; } = AcquisitionProtocol.Torrent;
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    /// <summary>"video" | "audio" | "subtitle".</summary>
    public string FileType { get; set; } = "video";
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>Vertical resolution (pixel height) of the release this file came from, per the ranker. 0 = unknown.</summary>
    public int ResolutionHeight { get; set; }
    public string? ReleaseName { get; set; }
    public string? SourceId { get; set; }
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

// ---------------------------------------------------------------------------------------------
// Background Jobs — admin UI read/write models for the generic scheduler, the "Missing" (still-searching)
// list, the "Cutoff Unmet" (quality-upgrade candidates) list, and the run-history feed.
// ---------------------------------------------------------------------------------------------

/// <summary>A recurring scheduled job as shown/edited in the admin Jobs panel.</summary>
public class ScheduledJobDto
{
    public int Id { get; set; }
    public JobType JobType { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; }
    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public JobRunStatus? LastStatus { get; set; }
    public string? LastMessage { get; set; }
    public bool IsRunning { get; set; }

    /// <summary>When the in-flight run started; null when idle.</summary>
    public DateTime? RunningSince { get; set; }
    /// <summary>Hard cap on one run, in seconds. Past this the panel treats the job as stuck.</summary>
    public int TimeoutSeconds { get; set; }
    /// <summary>Wall-clock duration of the last completed run.</summary>
    public int LastRunDurationMs { get; set; }
    /// <summary>Consecutive failed runs; 0 after any success.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Running for longer than its own timeout allows — i.e. the flag is almost certainly stale
    /// (a process died mid-run) rather than the job genuinely still working. Surfaces the Clear action.</summary>
    public bool LooksStuck => IsRunning && RunningSince is DateTime since
        && DateTime.UtcNow - since > TimeSpan.FromSeconds(Math.Max(60, TimeoutSeconds));
}

/// <summary>One execution in the job-run history feed.</summary>
public class JobRunDto
{
    public int Id { get; set; }
    public JobType JobType { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public JobRunStatus Status { get; set; }
    public int ItemsProcessed { get; set; }
    public string? Message { get; set; }
    public bool TriggeredManually { get; set; }
}

/// <summary>A request the downloader couldn't find a release for yet — parked and being re-searched on a
/// backoff (the "Missing" list). Never a hard failure.</summary>
public class WantedItemDto
{
    public int JobId { get; set; }
    public int RequestId { get; set; }
    public int MediaId { get; set; }
    public MediaType MediaType { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? PosterUrl { get; set; }
    public string? RequestedBy { get; set; }
    public Quality TargetQuality { get; set; }
    /// <summary>How many times the search has come up empty so far.</summary>
    public int DeferCount { get; set; }
    /// <summary>When the next automatic re-search is due.</summary>
    public DateTime? NextRetryAt { get; set; }
    /// <summary>Last recorded reason nothing was found (for the admin to diagnose).</summary>
    public string? LastError { get; set; }
    /// <summary>True once escalated to admins after many empty searches — a "needs attention" flag.</summary>
    public bool Escalated { get; set; }
    public DateTime RequestedAt { get; set; }
}

/// <summary>An available request whose imported quality is below its preferred target — a candidate for an
/// automatic quality upgrade (the "Cutoff Unmet" list).</summary>
public class CutoffUnmetItemDto
{
    public int RequestId { get; set; }
    public int MediaId { get; set; }
    public MediaType MediaType { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? PosterUrl { get; set; }
    public string? RequestedBy { get; set; }
    public Quality HaveQuality { get; set; }
    public Quality WantQuality { get; set; }
    public DateTime? LastUpgradeSearchAt { get; set; }
    public int UpgradeAttempts { get; set; }
    /// <summary>True while an upgrade job for this request is currently queued/in-flight.</summary>
    public bool UpgradeInProgress { get; set; }
}

// ---- Interactive search + blocklist -----------------------------------------------------------------

/// <summary>A search task handed to the downloader. The worker polls for these; the web app can't call it.</summary>
public class SearchTaskDto
{
    public int Id { get; set; }
    public SearchTaskKind Kind { get; set; }
    public int? MediaRequestId { get; set; }
    public MediaType MediaType { get; set; }
    public int MediaId { get; set; }
    public MediaRef? Media { get; set; }
    public RequestScopeKind RequestScope { get; set; }
    public MusicAcquisitionContextDto? Music { get; set; }
    public List<AcquisitionProtocol> AllowedAcquisitionProtocols { get; set; } = [AcquisitionProtocol.Torrent];
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? ImdbId { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public int? IndexerId { get; set; }
    public bool IncludeRejected { get; set; } = true;

    /// <summary>Profile to evaluate against, so an interactive search shows the same accept/reject calls
    /// the automatic search would make.</summary>
    public QualityProfileDto? QualityProfile { get; set; }
    public List<QualityDefinitionDto> QualityDefinitions { get; set; } = new();
    public List<string> BlocklistedHashes { get; set; } = new();
}

/// <summary>One release from an interactive search, with the verdict and the reasoning behind it.</summary>
public class SearchResultDto
{
    public string ReleaseName { get; set; } = string.Empty;
    public string Magnet { get; set; } = string.Empty;
    public string? InfoHash { get; set; }
    public AcquisitionProtocol Protocol { get; set; } = AcquisitionProtocol.Torrent;
    public int IndexerId { get; set; }
    public string IndexerName { get; set; } = string.Empty;
    public int Seeders { get; set; }
    public bool SeedersKnown { get; set; } = true;
    public double SizeGb { get; set; }
    public bool SizeKnown { get; set; } = true;
    public DateTime? PublishDate { get; set; }
    public int Resolution { get; set; }
    public string? QualityTier { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public bool IsPack { get; set; }

    public bool Accepted { get; set; }
    /// <summary>Every reason this was rejected, human-readable. Empty when accepted.</summary>
    public List<string> Rejections { get; set; } = new();
    public double Score { get; set; }
    /// <summary>Score contributions, so "why did that one win" is answerable.</summary>
    public Dictionary<string, double> ScoreBreakdown { get; set; } = new();
}

/// <summary>Worker → web: the outcome of a search task.</summary>
public class SearchTaskResultDto
{
    public List<SearchResultDto> Results { get; set; } = new();
    /// <summary>Per-indexer breakdown, e.g. "EZTV: 0, PirateBay: 12, 1337x: error".</summary>
    public string? IndexerSummary { get; set; }
    public string? Error { get; set; }
    /// <summary>Set only for a <see cref="SearchTaskKind.CapabilitiesTest"/>.</summary>
    public IndexerCapabilitiesDto? Capabilities { get; set; }
}

/// <summary>
/// What an indexer says it can do, from a Torznab <c>t=caps</c> probe (scrapers answer with a fixed
/// self-description plus a live reachability check).
///
/// This exists because indexer categories are the single most common misconfiguration: a tracker that
/// numbers its categories privately returns nothing for the Torznab standards, and an empty result set is
/// indistinguishable from "nothing matched". Reading the real category list off the endpoint turns that
/// into a picker instead of a guess.
/// </summary>
public class IndexerCapabilitiesDto
{
    public bool Reachable { get; set; }
    /// <summary>Human-readable outcome, shown verbatim next to the Test button.</summary>
    public string Message { get; set; } = string.Empty;
    public int? ResponseMs { get; set; }
    public bool SupportsMovieSearch { get; set; }
    public bool SupportsTvSearch { get; set; }
    public bool SupportsMusicSearch { get; set; }
    /// <summary>Search params the endpoint advertises (imdbid, season, ep, …) — informational.</summary>
    public List<string> SupportedParams { get; set; } = new();
    public List<IndexerCategoryDto> Categories { get; set; } = new();
}

public class IndexerCategoryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Null for a top-level category; otherwise the parent's id.</summary>
    public int? ParentId { get; set; }
}

/// <summary>Admin view of the search task while it's running or once it's done.</summary>
public class SearchTaskStatusDto
{
    public int Id { get; set; }
    public SearchTaskStatus Status { get; set; }
    public string? Error { get; set; }
    public string? IndexerSummary { get; set; }
    public List<SearchResultDto> Results { get; set; } = new();
    public IndexerCapabilitiesDto? Capabilities { get; set; }
}

/// <summary>Worker → web: a release that failed and must not be grabbed again.</summary>
public class BlocklistRequestDto
{
    public string? InfoHash { get; set; }
    public AcquisitionProtocol Protocol { get; set; } = AcquisitionProtocol.Torrent;
    public string? SourceId { get; set; }
    public string? ReleaseName { get; set; }
    public BlocklistReason Reason { get; set; }
    public string? Detail { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public int? IndexerId { get; set; }
    /// <summary>Optional retry boundary for transient source failures. Null keeps the existing permanent
    /// behavior used for broken torrent releases.</summary>
    public DateTime? ExpiresAt { get; set; }
}

public class BlocklistEntryDto
{
    public int Id { get; set; }
    public string? InfoHash { get; set; }
    public AcquisitionProtocol Protocol { get; set; } = AcquisitionProtocol.Torrent;
    public string? SourceId { get; set; }
    public string? ReleaseName { get; set; }
    public BlocklistScope Scope { get; set; }
    public BlocklistReason Reason { get; set; }
    public string? Detail { get; set; }
    public int? MediaRequestId { get; set; }
    public string? RequestTitle { get; set; }
    public int MediaId { get; set; }
    public MediaType MediaType { get; set; }
    public DateTime BlockedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}


/// <summary>One title the downloader saw trending on an indexer, before metadata resolution.</summary>
public class RecommendedFeedItemDto
{
    /// <summary>Title parsed out of the release name.</summary>
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public MediaType MediaType { get; set; }
    /// <summary>Position in the newest-first listing; lower is newer.</summary>
    public int Rank { get; set; }
    public int IndexerId { get; set; }
}

/// <summary>One episode automatic release monitoring should be watching for.</summary>
public class WantedEpisodeDto
{
    public int MediaRequestId { get; set; }
    public int ShowTmdbId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? ImdbId { get; set; }
    public bool IsAnime { get; set; }
    public int Season { get; set; }
    public int Episode { get; set; }
    public int? QualityProfileId { get; set; }
}

/// <summary>A release automatic monitoring matched against something wanted.</summary>
public class RssGrabDto
{
    public int MediaRequestId { get; set; }
    public int Season { get; set; }
    public int Episode { get; set; }
    public string ReleaseName { get; set; } = string.Empty;
    public string Magnet { get; set; } = string.Empty;
    public string? InfoHash { get; set; }
    public int IndexerId { get; set; }
}

/// <summary>Per-series monitoring state, for the media page's monitoring panel.</summary>
public class SeriesMonitoringDto
{
    public int RequestId { get; set; }
    public int ShowTmdbId { get; set; }
    public string Title { get; set; } = string.Empty;
    public MonitorMode MonitorMode { get; set; }
    public bool AutoMonitorNewSeasons { get; set; }
    public bool SearchForCutoffUpgrades { get; set; }
    public int? QualityProfileId { get; set; }
    public DateTime? MonitoredSince { get; set; }
    public DateTime? LastCheckedAt { get; set; }
}

/// <summary>One row of the air-date calendar.</summary>
public class AirScheduleEntryDto
{
    public int Id { get; set; }
    public int ShowTmdbId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? MediaRequestId { get; set; }
    public int Season { get; set; }
    public int Episode { get; set; }
    public string? EpisodeTitle { get; set; }
    public DateTime? AirDate { get; set; }
    /// <summary>Estimated moment to start looking — the air date plus an assumed broadcast time.</summary>
    public DateTime? AirsAtUtc { get; set; }
    public AirSearchState State { get; set; }
    public bool Monitored { get; set; }
    public bool HasFile { get; set; }
    public DateTime? NextSearchAt { get; set; }
    public int SearchAttempts { get; set; }
}

/// <summary>One condition inside a custom format.</summary>
public class FormatSpecificationDto
{
    public PlexRequestsHosted.Shared.Releases.FormatField Field { get; set; }
    public PlexRequestsHosted.Shared.Releases.FormatOp Op { get; set; }
    public string? Value { get; set; }
    /// <summary>Invert this condition.</summary>
    public bool Negate { get; set; }
    /// <summary>Must match for the format to apply at all. Non-required specs are an "any of" set.</summary>
    public bool Required { get; set; }
}

/// <summary>A user-defined scoring rule matched against release names and parsed attributes.</summary>
public class CustomFormatDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool IsSystem { get; set; }
    public List<FormatSpecificationDto> Specifications { get; set; } = new();
    /// <summary>Score this format carries in the profile currently being edited. Not persisted here —
    /// scores live per (profile, format).</summary>
    public int Score { get; set; }
}
