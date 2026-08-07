using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

/// <summary>
/// The rules that decide whether to abandon a download. These are worth pinning harder than most things
/// here: a false positive doesn't just drop one torrent, it blocklists the release so the system refuses
/// to pick it again — turning a transient network blip into permanent, silent unavailability of a title.
///
/// The live deployment runs 20 concurrent downloads through one VPN tunnel, where at any instant most
/// torrents are legitimately idle (queued behind the cap, or in a thin swarm), and VPN reconnects drop
/// every peer at once. Every "keep" case below is drawn from that reality.
/// </summary>
public class TorrentHealthTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
    private static DateTime Ago(TimeSpan t) => Now - t;

    private static TorrentObservation Obs(
        string state = "downloading", double progress = 10, int seeds = 0, int peers = 0,
        bool finished = false, bool present = true, bool metadata = true, string? tracker = null) =>
        new()
        {
            ClientState = state, Progress = progress, Seeds = seeds, Peers = peers,
            IsFinished = finished, PresentInClient = present, HasMetadata = metadata, TrackerStatus = tracker
        };

    // ---- Must never be failed --------------------------------------------------------------------

    [Theory]
    [InlineData("queued")]
    [InlineData("paused")]
    [InlineData("checking")]
    public void A_torrent_we_told_to_wait_is_never_judged(string state)
    {
        // 66 of 67 Reno 911 torrents were Queued behind max_active_downloading. Failing those would have
        // blocklisted an entire series' worth of good releases for the crime of being in a queue.
        var d = TorrentHealth.Decide(Obs(state, progress: 0), Ago(TimeSpan.FromDays(2)), null, Now);
        Assert.Equal(TorrentVerdict.Keep, d.Verdict);
    }

    [Fact]
    public void Zero_peers_alone_is_not_death()
    {
        // Between announces a healthy torrent can briefly show no peers.
        var d = TorrentHealth.Decide(Obs(progress: 40), Ago(TimeSpan.FromHours(3)),
            progressChangedAt: Ago(TimeSpan.FromMinutes(2)), Now);
        Assert.Equal(TorrentVerdict.Keep, d.Verdict);
    }

    [Fact]
    public void No_progress_alone_is_not_death()
    {
        // A slow swarm with peers attached is still alive; it just isn't fast.
        var d = TorrentHealth.Decide(Obs(peers: 3, seeds: 1), Ago(TimeSpan.FromHours(5)),
            progressChangedAt: Ago(TimeSpan.FromHours(4)), Now);
        Assert.Equal(TorrentVerdict.Keep, d.Verdict);
    }

    [Fact]
    public void A_stall_shorter_than_the_window_survives_a_vpn_reconnect()
    {
        // A reconnect drops every peer at once and recovery takes minutes. The window must outlast that.
        var d = TorrentHealth.Decide(Obs(), Ago(TimeSpan.FromHours(2)),
            progressChangedAt: Ago(TimeSpan.FromMinutes(10)), Now);
        Assert.Equal(TorrentVerdict.Keep, d.Verdict);
    }

    // ---- Must be acted on ------------------------------------------------------------------------

    [Fact]
    public void A_long_stall_with_nobody_to_talk_to_is_failed_and_blocklisted()
    {
        var d = TorrentHealth.Decide(Obs(), Ago(TimeSpan.FromHours(4)),
            progressChangedAt: Ago(TimeSpan.FromHours(2)), Now);
        Assert.Equal(TorrentVerdict.Fail, d.Verdict);
        Assert.True(d.Blocklist);
        Assert.Contains("No progress or peers", d.Reason);
    }

    [Fact]
    public void A_magnet_whose_metadata_never_arrives_is_a_dead_link()
    {
        // The user's "broken links" case: the magnet resolves to nothing at all.
        var d = TorrentHealth.Decide(Obs(progress: 0, metadata: false), Ago(TimeSpan.FromMinutes(30)), null, Now);
        Assert.Equal(TorrentVerdict.Fail, d.Verdict);
        Assert.True(d.Blocklist);
        Assert.Contains("metadata", d.Reason);
    }

    [Fact]
    public void Metadata_still_resolving_inside_its_budget_is_left_alone()
    {
        var d = TorrentHealth.Decide(Obs(progress: 0, metadata: false), Ago(TimeSpan.FromMinutes(5)), null, Now);
        Assert.Equal(TorrentVerdict.Keep, d.Verdict);
    }

    [Fact]
    public void The_clients_own_error_state_needs_no_window() =>
        Assert.Equal(TorrentVerdict.Fail,
            TorrentHealth.Decide(Obs("error"), Ago(TimeSpan.FromMinutes(1)), null, Now).Verdict);

    [Fact]
    public void A_torrent_removed_from_the_client_is_gone_not_failed()
    {
        // Distinct from Fail: nothing was wrong with the release, so it must NOT be blocklisted — the
        // operator may simply have removed it by hand.
        var d = TorrentHealth.Decide(Obs(present: false), Ago(TimeSpan.FromHours(1)), null, Now);
        Assert.Equal(TorrentVerdict.Gone, d.Verdict);
        Assert.False(d.Blocklist);
    }

    // ---- Finished wins over everything -----------------------------------------------------------

    [Fact]
    public void A_finished_torrent_imports_even_when_it_looks_unhealthy()
    {
        // This is the live case: nine Reno 911 episodes finished, then sat at 0 peers with a tracker error
        // while their job was marked "no acceptable release found" — and none of them were ever imported.
        var d = TorrentHealth.Decide(
            Obs("seeding", progress: 100, finished: true, tracker: "Error: Host not found (authoritative)"),
            Ago(TimeSpan.FromDays(1)), progressChangedAt: Ago(TimeSpan.FromHours(6)), Now);

        Assert.Equal(TorrentVerdict.Import, d.Verdict);
    }

    [Fact]
    public void Progress_at_100_counts_as_finished_even_if_the_flag_lags() =>
        Assert.Equal(TorrentVerdict.Import,
            TorrentHealth.Decide(Obs(progress: 100), Ago(TimeSpan.FromHours(2)), null, Now).Verdict);
}
