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
}
