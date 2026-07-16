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
    PartiallyAvailable = 8
}

public enum MediaType
{
    Movie = 0,
    TvShow = 1,
    Music = 2,
    Anime = 3
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
    RequestAvailable = 7
}

public enum BridgeEventType
{
    Created = 0,
    Approved = 1,
    Denied = 2,
    Available = 3,
    Failed = 4
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
    PartiallyCompleted = 6
}

public enum IssueStatus
{
    Open = 0,
    Resolved = 1,
    Dismissed = 2
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
