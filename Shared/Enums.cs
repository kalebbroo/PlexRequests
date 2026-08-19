namespace PlexRequestsHosted.Shared.Enums;

public enum RequestStatus
{
    None = 0,
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Processing = 4,
    Available = 5,
    Failed = 6,
    Cancelled = 7,
    /// <summary>Some but not all of the requested seasons/episodes imported before the rest failed —
    /// distinct from a hard Failed so the UI can offer "retry the rest" instead of "retry everything".</summary>
    PartiallyAvailable = 8,
    /// <summary>Approved and being worked, but no release is findable yet (e.g. not released, or no seeders
    /// yet). Not a failure — the downloader keeps re-searching on a backoff until it appears or an admin
    /// cancels. Distinct from Processing (which means a download is actively in flight).</summary>
    Searching = 9
}

public enum MediaType
{
    Movie = 0,
    TvShow = 1,
    Music = 2,
    Anime = 3
}

/// <summary>
/// The shape of a catalog item. <see cref="MediaType"/> remains the broad UI/library grouping, while this
/// identifies what can actually be requested. In particular, music is not one shape: artists, albums and
/// tracks have different request scopes and fulfillment rules.
/// </summary>
public enum MediaKind
{
    Movie = 0,
    Series = 1,
    Album = 2,
    Artist = 3,
    Track = 4
}

/// <summary>The durable intent of a request, independent of the metadata provider that identified it.</summary>
public enum RequestScopeKind
{
    Title = 0,
    Series = 1,
    Seasons = 2,
    Episodes = 3,
    Album = 4,
    ArtistCatalog = 5,
    Track = 6
}

[Flags]
public enum UserRole
{
    User = 1,
    PowerUser = 2,
    Admin = 4,
    AutoApprove = 8,
    RequestMovie = 16,
    RequestTV = 32,
    RequestMusic = 64,
    Guest = 128
}

public enum NotificationType
{
    Info = 0,
    Success = 1,
    Warning = 2,
    Error = 3,
    RequestCreated = 4,
    RequestApproved = 5,
    RequestRejected = 6,
    RequestAvailable = 7,
    /// <summary>A request has been searching for a long time with no findable release — surfaced to admins
    /// so they can intervene (add an indexer, adjust quality rules) instead of the request silently failing.</summary>
    RequestSearchStalled = 8,
    /// <summary>An already-available title was auto-upgraded to a better-quality release matching preferences.</summary>
    RequestUpgraded = 9
}

public enum BridgeEventType
{
    Created = 0,
    Approved = 1,
    Denied = 2,
    Available = 3,
    Failed = 4,
    Searching = 5,
    PartiallyAvailable = 6,
    Upgraded = 7,
    Cancelled = 8
}

public enum FulfillmentStatus
{
    Queued = 0,
    Claimed = 1,
    Downloading = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
    /// <summary>One or more torrents imported before another in the same job failed/errored — not a
    /// clean Completed, but not a total loss either.</summary>
    PartiallyCompleted = 6,
    /// <summary>No acceptable release found yet; parked until <c>NextRetryAt</c>, then re-searched. Counts
    /// as an active job (won't be duplicate-enqueued), but the reaper ignores it (it isn't Claimed).</summary>
    Deferred = 7
}

public enum IssueStatus
{
    Open = 0,
    Resolved = 1,
    Dismissed = 2
}

/// <summary>A kind of recurring/on-demand background job the generic scheduler can run. Each value maps to
/// exactly one <c>IJobHandler</c> implementation, resolved by type at dispatch time. Add a value here + a
/// handler class to introduce a new job type (e.g. a future LibraryScan or MetadataRefresh).</summary>
public enum JobType
{
    /// <summary>Re-search for requests parked in <see cref="FulfillmentStatus.Deferred"/> whose backoff has
    /// elapsed: flip them back to Queued so the downloader searches them again. Powers "never dead-end".</summary>
    MissingSearch = 0,
    /// <summary>Find already-available titles downloaded below their preferred quality and enqueue an upgrade
    /// search to auto-replace them with a better release.</summary>
    QualityUpgradeScan = 1,

    /// <summary>Check monitored series for episodes that have aired but aren't on Plex, and enqueue them.</summary>
    SeriesMonitor = 2,

    /// <summary>Rebuild the DB-backed Plex availability index (item id-maps + per-season episode presence).</summary>
    AvailabilityRefresh = 3,

    /// <summary>Safety net: mark open requests Available when their content shows up on Plex by any route.</summary>
    AvailabilityReconcile = 4,

    /// <summary>Requeue or park fulfillment jobs stranded by a downloader that died mid-work.</summary>
    StaleClaimReaper = 5,

    /// <summary>Delete expired interactive-search tasks and lapsed blocklist entries.</summary>
    SearchTaskCleanup = 6,

    /// <summary>Refresh the air-date calendar for monitored series from live metadata.</summary>
    CalendarRefresh = 7,

    /// <summary>Queue searches for episodes whose air window has opened.</summary>
    AirDateMonitor = 8,

    /// <summary>Re-derive quality tier and custom-format score for already-imported files from their stored
    /// release names, so a scoring-rule change applies to the existing library and not just to new grabs.</summary>
    RecomputeFormatScores = 9
}

/// <summary>
/// Whether a request's imported files meet its preferred quality. This is deliberately three-valued: the old
/// boolean had no way to say "we can't tell", so an import whose resolution never parsed (a release name with
/// no 1080p/720p token) was indistinguishable from one that genuinely met the target — and the upgrade scanner
/// resolved that ambiguity by marking it met, permanently excluding the request from ever being upgraded.
/// </summary>
public enum CutoffState
{
    /// <summary>Nothing imported yet, or nothing imported with a known resolution. Not a claim either way —
    /// eligible for a re-derive pass rather than being treated as satisfied.</summary>
    Unknown = 0,
    /// <summary>Every imported video file is at or above the preferred quality.</summary>
    Met = 1,
    /// <summary>At least one imported video file is below the preferred quality — an upgrade candidate.</summary>
    Unmet = 2
}

/// <summary>
/// Where one torrent stands, as the reconciler sees it. Deliberately separate from the JOB's status: a job
/// is Deferred/Completed as a whole, while its individual torrents succeed and fail independently — and
/// conflating the two is how a job came to be marked "no acceptable release found" while nine of its
/// torrents had finished downloading.
/// </summary>
public enum TorrentTrackingState
{
    /// <summary>In the client and progressing (or legitimately queued behind it).</summary>
    Active = 0,
    /// <summary>Finished downloading; not yet moved into the library.</summary>
    Finished = 1,
    /// <summary>Files placed in the library. Terminal, successful.</summary>
    Imported = 2,
    /// <summary>Gave up on this torrent — stalled, errored, or unimportable. Terminal.</summary>
    Failed = 3,
    /// <summary>The client no longer has it: removed by hand, or its state was lost. Terminal; the job
    /// re-plans for whatever this was supposed to cover rather than waiting on something that is gone.</summary>
    Missing = 4
}

/// <summary>Where an episode stands in the monitor's pipeline.</summary>
public enum AirSearchState
{
    /// <summary>Hasn't aired yet (or its window hasn't opened).</summary>
    NotDue = 0,
    /// <summary>Aired and wanted; the monitor should queue it.</summary>
    Due = 1,
    /// <summary>A request has been created and is being worked.</summary>
    Searching = 2,
    /// <summary>On Plex.</summary>
    Acquired = 3,
    /// <summary>Deliberately not wanted because the selected monitor mode excludes it.</summary>
    Skipped = 4,
    /// <summary>
    /// The metadata source has no air date. Previously these were invisible: HasAired is false without a
    /// date, and the monitor only queued episodes where it was true — so an undated episode was never
    /// fetched, ever. They now get a slow periodic sweep instead of being silently dropped.
    /// </summary>
    AirDateUnknown = 5
}

/// <summary>Which episodes of a series the monitor should keep chasing.</summary>
public enum MonitorMode
{
    /// <summary>Nothing — the request is a one-off.</summary>
    None = 0,
    /// <summary>Everything, past and future.</summary>
    AllEpisodes = 1,
    /// <summary>Only episodes that air from now on.</summary>
    FutureEpisodes = 2,
    /// <summary>Only what's missing from Plex today; nothing new.</summary>
    MissingEpisodes = 3,
    /// <summary>Only episodes already on Plex (upgrades only).</summary>
    ExistingEpisodes = 4,
    FirstSeason = 5,
    LatestSeason = 6
}

/// <summary>What a queued search task is for.</summary>
public enum SearchTaskKind
{
    /// <summary>An admin asked to see every release for a title, accepted or not.</summary>
    InteractiveSearch = 0,
    /// <summary>Ask one indexer what categories it supports, so the admin can pick from a real list.</summary>
    CapabilitiesTest = 1
}

public enum SearchTaskStatus
{
    Pending = 0,
    Claimed = 1,
    Completed = 2,
    Failed = 3,
    /// <summary>Nobody picked it up before it aged out — usually means the downloader isn't running.</summary>
    Expired = 4
}

/// <summary>How widely a blocklist entry applies.</summary>
public enum BlocklistScope
{
    /// <summary>Only this request. The default: a release that failed here may be fine elsewhere.</summary>
    Request = 0,
    /// <summary>Every request for this title.</summary>
    Media = 1,
    /// <summary>Everywhere. For releases that are simply bad (fake, malware, mislabelled).</summary>
    Global = 2
}

public enum BlocklistReason
{
    DownloadFailed = 0,
    ImportFailed = 1,
    Stalled = 2,
    TorrentError = 3,
    /// <summary>Finished but nothing usable could be found on disk.</summary>
    PathUnresolvable = 4,
    /// <summary>Downloaded the wrong thing.</summary>
    WrongContent = 5,
    ManualBlock = 6
}

/// <summary>Outcome of a single scheduled-job execution, recorded in the job-run history.</summary>
public enum JobRunStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    /// <summary>Ran but did nothing meaningful (nothing due, feature disabled) — not an error.</summary>
    Skipped = 3
}

/// <summary>Which HDR variant a release carries, if any. Ordered by desirability, so a comparison picks
/// the better one. <c>ParsedRelease.Hdr</c> is derived from this, so existing preferences keep working.</summary>
public enum HdrFormat
{
    None = 0,
    Sdr = 1,
    Hlg = 2,
    Hdr10 = 3,
    Hdr10Plus = 4,
    DolbyVision = 5
}

/// <summary>
/// Where a release was sourced from, ordered worst-to-best so the integer value doubles as a ranking weight.
/// Lives here rather than in the downloader because it is one half of a quality tier (resolution x source):
/// the web app defines and orders those tiers, the downloader matches releases against them.
/// </summary>
/// <summary>
/// Why an indexer refused to answer. Kept distinct from "answered with nothing" because conflating the two
/// is what let two permanently-blocked indexers sit green in the admin panel through thirteen searches
/// apiece. Each value implies a different remedy, which is the point of naming them.
/// </summary>
public enum IndexerBlockReason
{
    None = 0,
    /// <summary>Cloudflare interstitial. Solvable: a real browser can earn a clearance cookie.</summary>
    CloudflareChallenge = 1,
    /// <summary>Plain 403 with no challenge — credentials or the site simply says no.</summary>
    Forbidden = 2,
    /// <summary>429/backoff. Transient; slow down rather than escalate.</summary>
    RateLimited = 3,
    /// <summary>Cloudflare error 1020 or equivalent. The exit IP is banned; a browser will not help.</summary>
    IpBanned = 4
}

public enum ReleaseSource
{
    Unknown = 0,
    Cam = 1,
    Hdtv = 2,
    WebRip = 3,
    WebDl = 4,
    BluRay = 5,
    Remux = 6
}

public enum Quality
{
    Any = 0,
    SD = 480,
    HD = 720,
    FullHD = 1080,
    UHD4K = 2160,
    UHD8K = 4320
}

/// <summary>How the downloader chooses between a full-season pack and individual episodes for a season-scoped job.</summary>
public enum SeasonPackStrategy
{
    /// <summary>Prefer a full-season pack; fall back to individual episodes only if no acceptable pack exists.</summary>
    PreferPack = 0,
    /// <summary>Prefer individual episodes; use a pack only to fill episodes that have no standalone release.</summary>
    PreferEpisodes = 1,
    /// <summary>Pick a pack when one is available within the size cap, else fan out to episodes.</summary>
    Auto = 2
}

/// <summary>Network file-sharing protocol used to reach an admin-configured NAS/network drive.</summary>
public enum NetworkShareProtocol
{
    /// <summary>SMB/CIFS — Windows shares, Synology/QNAP/unRAID/TrueNAS default. The common case.</summary>
    Smb = 0,
    /// <summary>NFS export (Linux/Unix-style). No username/password; access is host/IP based.</summary>
    Nfs = 1
}

/// <summary>How the organizer places a finished download's video files into the library.</summary>
public enum TransferMode
{
    /// <summary>Hardlink into the library (keeps the torrent seeding); falls back to Copy when the
    /// library path is on a different filesystem or the host OS doesn't support it.</summary>
    Hardlink = 0,
    Copy = 1,
    Move = 2
}

public enum SortOrder
{
    DateDescending = 0,
    DateAscending = 1,
    TitleAscending = 2,
    TitleDescending = 3,
    RatingDescending = 4,
    PopularityDescending = 5,
    RecentlyAdded = 6,
    Trending = 7
}
