using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared;

/// <summary>
/// Fulfillment states that mean a download, replacement, or re-search is still in flight for a request. Enqueueing,
/// storage optimization, and availability reconciliation all ask this question, so they share one answer.
/// </summary>
public static class FulfillmentJobPolicy
{
    public static readonly FulfillmentStatus[] ActiveStatuses =
        [FulfillmentStatus.Queued, FulfillmentStatus.Claimed, FulfillmentStatus.Downloading, FulfillmentStatus.Deferred];

    /// <summary>
    /// Jobs with work actually under way. A request may own only one of these at a time (the database enforces it),
    /// so a parked Deferred job must wait for these to finish before it is requeued. Replacements are not blocked
    /// by a parked job, because a parked job is waiting for a search, not doing work.
    /// </summary>
    public static readonly FulfillmentStatus[] InFlightStatuses =
        [FulfillmentStatus.Queued, FulfillmentStatus.Claimed, FulfillmentStatus.Downloading];
}
