using System.Net;
using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

/// <summary>
/// Pins "blocked is not empty".
///
/// The providers used to catch a 403, log a warning and return an empty list, so the client recorded a
/// *successful search that found nothing*. 1337x and ext.to each ran 13 searches that way and still showed
/// a clean health record in the admin panel — the single reason the outage went unnoticed for weeks.
///
/// The distinction between a challenge and a hard IP ban matters just as much because they require
/// different backoff windows and operator guidance.
/// </summary>
public class IndexerBlockDetectionTests
{
    private static Dictionary<string, string> H(params (string K, string V)[] pairs) =>
        pairs.ToDictionary(p => p.K, p => p.V, StringComparer.OrdinalIgnoreCase);

    // Captured verbatim from 1337x on the live box.
    private const string RealChallengeBody =
        "<!DOCTYPE html><html><head><title>Just a moment...</title></head>" +
        "<body><script src=\"/cdn-cgi/challenge-platform/h/g/orchestrate/chl_page/v1\"></script>" +
        "<form id=\"challenge-form\" action=\"/?__cf_chl_f_tk=abc\"></form>Enable JavaScript and cookies to continue</body></html>";

    [Fact]
    public void The_real_1337x_response_is_a_cloudflare_challenge()
    {
        var reason = IndexerFetch.DetectBlock(
            HttpStatusCode.Forbidden,
            H(("server", "cloudflare"), ("cf-mitigated", "challenge")),
            RealChallengeBody);

        Assert.Equal(IndexerBlockReason.CloudflareChallenge, reason);
    }

    [Fact]
    public void A_hard_ip_ban_is_not_reported_as_a_solvable_challenge()
    {
        // Retrying this with a heavier client would burn time per search and never succeed.
        var reason = IndexerFetch.DetectBlock(
            HttpStatusCode.Forbidden,
            H(("server", "cloudflare")),
            "<html><body>error code: 1020</body></html>");

        Assert.Equal(IndexerBlockReason.IpBanned, reason);
    }

    [Fact]
    public void The_cf_mitigated_header_alone_is_enough()
    {
        // Cloudflare sometimes returns the challenge with no recognisable body (or an empty one).
        Assert.Equal(IndexerBlockReason.CloudflareChallenge,
            IndexerFetch.DetectBlock(HttpStatusCode.Forbidden, H(("cf-mitigated", "challenge")), null));
    }

    [Theory]
    [InlineData(429, IndexerBlockReason.RateLimited)]
    [InlineData(503, IndexerBlockReason.CloudflareChallenge)]   // with the cloudflare server header
    public void Transient_refusals_get_their_own_reason(int status, IndexerBlockReason expected) =>
        Assert.Equal(expected, IndexerFetch.DetectBlock((HttpStatusCode)status, H(("server", "cloudflare")), null));

    [Fact]
    public void A_plain_403_from_a_non_cloudflare_host_is_forbidden_not_a_challenge() =>
        Assert.Equal(IndexerBlockReason.Forbidden,
            IndexerFetch.DetectBlock(HttpStatusCode.Forbidden, H(("server", "nginx")), "nope"));

    [Fact]
    public void A_healthy_empty_result_is_not_a_block()
    {
        // The case that must stay distinguishable: the site answered, there was simply nothing to find.
        Assert.Null(IndexerFetch.DetectBlock(HttpStatusCode.OK, H(("server", "cloudflare")),
            "<html><body><table id=\"results\"></table></body></html>"));
    }

    [Fact]
    public void A_page_that_merely_mentions_the_words_is_not_a_block()
    {
        // A torrent legitimately named "Just a Moment (2019) 1080p" must not take an indexer offline.
        Assert.Null(IndexerFetch.DetectBlock(HttpStatusCode.OK, H(("server", "nginx")),
            "<html><body><a>Just a Moment 2019 1080p WEB-DL</a></body></html>"));
    }
}
