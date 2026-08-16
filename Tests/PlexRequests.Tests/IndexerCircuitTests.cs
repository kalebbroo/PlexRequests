using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class IndexerCircuitTests
{
    [Theory]
    [InlineData(IndexerBlockReason.CloudflareChallenge, 6)]
    [InlineData(IndexerBlockReason.Forbidden, 6)]
    [InlineData(IndexerBlockReason.IpBanned, 12)]
    public void Hard_blocks_receive_long_persisted_cooldowns(IndexerBlockReason reason, int hours) =>
        Assert.Equal(TimeSpan.FromHours(hours), IndexerAdminService.SearchFailureDelay(reason, 1));

    [Fact]
    public void Repeated_transient_failures_back_off_but_remain_self_healing()
    {
        Assert.Equal(TimeSpan.FromMinutes(1),
            IndexerAdminService.SearchFailureDelay(IndexerBlockReason.None, 3));
        Assert.Equal(TimeSpan.FromMinutes(60),
            IndexerAdminService.SearchFailureDelay(IndexerBlockReason.None, 20));
    }
}
