using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

/// <summary>
/// A block is a verdict on the release. An infrastructure failure is not a verdict, so it must lapse. A content failure is,
/// so it must not. Getting this wrong blocks a good link forever.
/// </summary>
public sealed class BlocklistExpiryTests
{
    private static readonly DateTime Now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(BlocklistReason.Stalled)]
    [InlineData(BlocklistReason.TorrentError)]
    [InlineData(BlocklistReason.PathUnresolvable)]
    [InlineData(BlocklistReason.DownloadFailed)]
    [InlineData(BlocklistReason.ImportFailed)]
    public void An_infrastructure_failure_on_a_torrent_expires_so_the_link_is_tried_again(BlocklistReason reason)
    {
        Assert.Equal(Now + BlocklistExpiry.TransientDuration, BlocklistExpiry.For(reason, AcquisitionProtocol.Torrent, Now));
    }

    [Theory]
    [InlineData(BlocklistReason.WrongContent)]
    [InlineData(BlocklistReason.MediaPolicyMismatch)]
    [InlineData(BlocklistReason.EpisodeMappingAmbiguous)]
    [InlineData(BlocklistReason.ManualBlock)]
    public void A_content_failure_stays_blocked_for_good(BlocklistReason reason)
    {
        Assert.Null(BlocklistExpiry.For(reason, AcquisitionProtocol.Torrent, Now));
    }

    [Fact]
    public void A_remote_direct_failure_has_always_expired_and_still_does()
    {
        Assert.Equal(Now + BlocklistExpiry.TransientDuration,
            BlocklistExpiry.For(BlocklistReason.WrongContent, AcquisitionProtocol.DirectVideo, Now));
    }
}
