using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class IndexerRuntimeCircuitTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Cloudflare_block_opens_immediately_for_the_shared_six_hour_window()
    {
        var circuit = new IndexerRuntimeCircuit();

        circuit.Record([Report(7, success: false, IndexerBlockReason.CloudflareChallenge, Now)]);

        Assert.False(circuit.Allows(7, Now));
        Assert.False(circuit.Allows(7, Now.AddHours(6).AddTicks(-1)));
        Assert.True(circuit.Allows(7, Now.AddHours(6)));
    }

    [Fact]
    public void A_later_success_closes_the_runtime_circuit()
    {
        var circuit = new IndexerRuntimeCircuit();
        circuit.Record([Report(7, success: false, IndexerBlockReason.Forbidden, Now)]);

        circuit.Record([Report(7, success: true, IndexerBlockReason.None, Now.AddMinutes(1))]);

        Assert.True(circuit.Allows(7, Now.AddMinutes(1)));
    }

    [Fact]
    public void A_stale_or_simultaneous_success_cannot_erase_a_newer_block_from_a_concurrent_search()
    {
        var circuit = new IndexerRuntimeCircuit();
        circuit.Record([Report(7, success: false, IndexerBlockReason.CloudflareChallenge, Now.AddSeconds(1))]);

        circuit.Record([Report(7, success: true, IndexerBlockReason.None, Now)]);
        circuit.Record([Report(7, success: true, IndexerBlockReason.None, Now.AddSeconds(1))]);

        Assert.False(circuit.Allows(7, Now.AddMinutes(1)));
    }

    [Fact]
    public void Ordinary_failures_do_not_bypass_the_durable_consecutive_failure_policy()
    {
        var circuit = new IndexerRuntimeCircuit();

        circuit.Record([Report(7, success: false, IndexerBlockReason.None, Now)]);

        Assert.True(circuit.Allows(7, Now));
    }

    [Fact]
    public void Circuits_are_isolated_by_stable_indexer_id()
    {
        var circuit = new IndexerRuntimeCircuit();

        circuit.Record([Report(7, success: false, IndexerBlockReason.RateLimited, Now)]);

        Assert.False(circuit.Allows(7, Now.AddMinutes(29)));
        Assert.True(circuit.Allows(8, Now.AddMinutes(29)));
    }

    private static IndexerStatusReportDto Report(
        int indexerId,
        bool success,
        IndexerBlockReason reason,
        DateTime searchedAt) => new()
    {
        IndexerId = indexerId,
        Name = $"indexer-{indexerId}",
        Success = success,
        BlockReason = reason,
        SearchedAt = searchedAt
    };
}
