using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared;

/// <summary>
/// One cooldown policy shared by the web app's durable indexer circuit and the downloader's immediate
/// in-process circuit. Keeping the durations here prevents the two halves from disagreeing about when an
/// upstream source may be probed again.
/// </summary>
public static class IndexerCircuitPolicy
{
    public static TimeSpan FailureDelay(IndexerBlockReason reason, int consecutiveFailures) => reason switch
    {
        IndexerBlockReason.CloudflareChallenge => TimeSpan.FromHours(6),
        IndexerBlockReason.IpBanned => TimeSpan.FromHours(12),
        IndexerBlockReason.Forbidden => TimeSpan.FromHours(6),
        IndexerBlockReason.RateLimited => TimeSpan.FromMinutes(30),
        _ => TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Clamp(consecutiveFailures - 3, 0, 6))))
    };
}
