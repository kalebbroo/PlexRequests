using PlexRequests.Downloader.Indexers;
using Xunit;

namespace PlexRequests.Tests;

public class PersistentBrowserTransportTests
{
    private static readonly Uri Origin = new("https://1337x.to/");

    [Theory]
    [InlineData("https://1337x.to/")]
    [InlineData("https://1337x.to/category-search/show/TV/1/")]
    public void Configured_origin_is_allowed(string url) =>
        Assert.True(PersistentBrowserTransport.IsAllowedLocation(url, Origin));

    [Theory]
    [InlineData("http://1337x.to/")]
    [InlineData("https://1337x.to.evil.example/")]
    [InlineData("https://example.com/")]
    [InlineData("not a url")]
    public void Navigation_cannot_escape_the_configured_origin(string url) =>
        Assert.False(PersistentBrowserTransport.IsAllowedLocation(url, Origin));

    [Fact]
    public void Cloudflare_interstitial_is_detected() =>
        Assert.True(PersistentBrowserTransport.LooksLikeChallenge(
            "<html><head><title>Just a moment...</title></head><body><script src='/cdn-cgi/challenge-platform/x'></script></body></html>"));

    [Fact]
    public void Real_results_win_over_a_shared_turnstile_script() =>
        Assert.False(PersistentBrowserTransport.LooksLikeChallenge(
            "<script src='cf-turnstile.js'></script><table class='table-list'><tr><td>release</td></tr></table>"));

    [Fact]
    public void Real_detail_page_is_not_a_challenge() =>
        Assert.False(PersistentBrowserTransport.LooksLikeChallenge(
            "<a href='magnet:?xt=urn:btih:ABCDEF'>download</a>"));
}
