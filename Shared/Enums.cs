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
    Cancelled = 7
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
    Cancelled = 5
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
