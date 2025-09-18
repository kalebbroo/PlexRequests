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
    RequestUpdate = 0,
    MediaAvailable = 1,
    NewContent = 2,
    System = 3,
    ApprovalNeeded = 4,
    Comment = 5
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
