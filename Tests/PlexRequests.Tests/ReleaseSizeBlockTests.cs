using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

/// <summary>A Plex-only copy has no infohash or source id, so its exact size is the identity that blocks it.</summary>
public sealed class ReleaseSizeBlockTests
{
    private const long FailedCopyBytes = 2L * 1024 * 1024 * 1024;

    [Fact]
    public void A_release_of_the_exact_size_of_a_failed_copy_is_rejected_even_with_no_source_identity_match()
    {
        var context = TestData.Context(sizes: new HashSet<long> { FailedCopyBytes });
        var sameBytes = TestData.Release("Halloweentown.1998.1080p.WEB-DL.x264-GRP", sizeGb: 2.0);
        var otherBytes = TestData.Release("Halloweentown.1998.1080p.WEB-DL.x264-OTHER", sizeGb: 2.5);

        var same = TestData.Evaluator().Evaluate(sameBytes, TestData.Job(), context);
        var other = TestData.Evaluator().Evaluate(otherBytes, TestData.Job(), context);

        Assert.Equal(FailedCopyBytes, sameBytes.SizeBytes);
        Assert.Contains(same.Rejections, r => r.Reason == RejectionReason.Blocklisted);
        Assert.DoesNotContain(other.Rejections, r => r.Reason == RejectionReason.Blocklisted);
    }
}

public sealed class ReleaseSizeBlocklistServiceTests
{
    private const long FailedCopyBytes = 2L * 1024 * 1024 * 1024;

    [Fact]
    public async Task A_size_block_is_recorded_once_for_its_request_with_no_infohash_or_source_id()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var request = new MediaRequestEntity
        {
            MediaId = 603, MediaType = MediaType.Movie, Title = "Halloweentown", Status = RequestStatus.Available
        };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();
        var service = new ReleaseBlocklistService(db, NullLogger<ReleaseBlocklistService>.Instance);
        var block = new BlocklistRequestDto
        {
            SizeBytes = FailedCopyBytes, ReleaseName = "Library copy of Halloweentown", Reason = BlocklistReason.WrongContent
        };

        Assert.True(await service.BlockForRequestAsync(request.Id, 603, MediaType.Movie, block));
        Assert.True(await service.BlockForRequestAsync(request.Id, 603, MediaType.Movie, block));

        var row = await db.ReleaseBlocklist.SingleAsync();
        Assert.Equal(FailedCopyBytes, row.SizeBytes);
        Assert.Null(row.InfoHash);
        Assert.Null(row.SourceId);
        Assert.Equal(request.Id, row.MediaRequestId);
    }

    [Fact]
    public async Task A_block_with_no_identity_name_or_size_is_refused_and_records_nothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var service = new ReleaseBlocklistService(db, NullLogger<ReleaseBlocklistService>.Instance);

        var recorded = await service.BlockForRequestAsync(7, 603, MediaType.Movie,
            new BlocklistRequestDto { Reason = BlocklistReason.WrongContent });

        Assert.False(recorded);
        Assert.Empty(await db.ReleaseBlocklist.ToListAsync());
    }
}
