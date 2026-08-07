using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Indexers;

/// <summary>A clearance won from a challenge: the cookie header to replay, and the User-Agent that earned it.</summary>
public sealed record Clearance(string CookieHeader, string UserAgent);

/// <summary>
/// Solves an interstitial challenge for an indexer and returns credentials that plain HTTP can then reuse.
///
/// The shape matters more than the implementation. Cloudflare's JS interstitial cannot be defeated by
/// better headers or TLS trickery — it requires executing the challenge, which means a browser engine. But
/// running a browser per search would be absurd, so the browser is used ONCE to earn a `cf_clearance`
/// cookie and everything afterwards is ordinary cheap HTTP carrying that cookie. This interface is that
/// boundary: expensive, rare, and cached behind a fast path.
/// </summary>
public interface IChallengeSolver
{
    /// <summary>Whether a solver is actually available in this deployment (a browser may not be installed).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Attempt to solve for <paramref name="url"/>. Returns null when it can't — which the caller must treat
    /// as "stay blocked", never as "retry forever".
    /// </summary>
    Task<Clearance?> SolveAsync(IndexerConfigDto indexer, string url, CancellationToken ct);
}

/// <summary>
/// The default when no browser is present. Deliberately explicit rather than an absent registration, so the
/// fetch path has one code shape and the panel can say "no solver available" instead of silently never
/// trying.
/// </summary>
public sealed class NullChallengeSolver : IChallengeSolver
{
    public bool IsAvailable => false;
    public Task<Clearance?> SolveAsync(IndexerConfigDto indexer, string url, CancellationToken ct) =>
        Task.FromResult<Clearance?>(null);
}
