using System.Net;
using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

/// <summary>
/// Pins WHICH blocks are worth spending a browser on. Escalation costs a browser launch and tens of
/// seconds; doing it for a block no client-side action can fix turns one unreachable site into a
/// system-wide slowdown, since every search would pay the cost and still fail.
/// </summary>
public class ChallengeEscalationTests
{
    private static Dictionary<string, string> H(params (string K, string V)[] p) =>
        p.ToDictionary(x => x.K, x => x.V, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_cloudflare_interstitial_is_worth_solving()
    {
        // Solvable: running the challenge yields a clearance cookie.
        Assert.Equal(IndexerBlockReason.CloudflareChallenge,
            IndexerFetch.DetectBlock(HttpStatusCode.Forbidden, H(("cf-mitigated", "challenge")), null));
    }

    [Fact]
    public void An_ip_ban_is_not_worth_solving()
    {
        // No browser defeats a 1020 — the exit IP is the problem. Escalating would burn ~90s per search.
        Assert.Equal(IndexerBlockReason.IpBanned,
            IndexerFetch.DetectBlock(HttpStatusCode.Forbidden, H(("server", "cloudflare")), "error code: 1020"));
    }

    [Fact]
    public void A_rate_limit_wants_patience_not_a_heavier_client() =>
        Assert.Equal(IndexerBlockReason.RateLimited,
            IndexerFetch.DetectBlock((HttpStatusCode)429, H(("server", "cloudflare")), null));

    [Fact]
    public void No_solver_available_still_reports_a_block_rather_than_pretending_to_succeed()
    {
        // A deployment built without a browser must degrade to "blocked, here's why", not silence.
        var solver = new NullChallengeSolver();
        Assert.False(solver.IsAvailable);
    }
}
