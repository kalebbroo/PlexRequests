namespace PlexRequestsHosted.Services.Jobs;

/// <summary>
/// The backoff schedule for re-searching a request whose release isn't findable yet. Instead of failing,
/// a job is parked (Deferred) with a <c>NextRetryAt</c> computed here, and re-queued once it passes. The
/// cadence grows with the number of empty searches so a genuinely-unavailable title doesn't hammer indexers,
/// and a title that hasn't been released yet is checked only about once a day.
/// </summary>
public static class RetryBackoff
{
    /// <summary>Empty searches after which the request is escalated to admins ("still searching, needs attention").</summary>
    public const int EscalateAfterDeferrals = 6;

    private const double BaseMinutes = 15;
    private const double MaxMinutes = 24 * 60;

    /// <summary>
    /// Next re-search time. If the title isn't out yet (release date in the future) it's checked ~daily;
    /// otherwise it's exponential — 15m, 30m, 1h, 2h, 4h, 8h, 16h — capped at 24h.
    /// </summary>
    public static DateTime ComputeNextRetry(int deferCount, DateTime? releaseDate, DateTime now)
    {
        if (releaseDate is DateTime rd && rd.Date > now.Date)
            return now.AddHours(24);

        var minutes = BaseMinutes * Math.Pow(2, Math.Clamp(deferCount, 0, 7));
        return now.AddMinutes(Math.Min(minutes, MaxMinutes));
    }
}
