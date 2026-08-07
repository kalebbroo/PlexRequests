using PlexRequests.Downloader.Download;
using Xunit;

namespace PlexRequests.Tests;

/// <summary>
/// Pins the Deluge error classification. This exists because of a real outage: the classifier was
/// <c>msg.Contains("auth") || msg.Contains("session")</c>, and Deluge answers a duplicate add with
/// "Torrent already in session (&lt;hash&gt;)". The bare word "session" made that look like an expired
/// login, so the client re-authenticated, retried the identical add, failed the same way, and the job
/// stayed at 0% while the torrent downloaded happily in the client in front of it.
///
/// The first test is the exact message taken from the production log.
/// </summary>
public class DelugeErrorTests
{
    private const string RealProductionMessage =
        "Failure: [Failure instance: Traceback (failure with no frames): " +
        "<class 'deluge.error.AddTorrentError'>: Torrent already in session (57a0d12b99fff5d559761b9470785439c1414aa1).";

    [Fact]
    public void The_message_that_caused_the_outage_is_a_duplicate_not_an_auth_failure()
    {
        Assert.Equal(DelugeErrorKind.AlreadyInSession, DelugeError.Classify(RealProductionMessage));
        Assert.NotEqual(DelugeErrorKind.NotAuthenticated, DelugeError.Classify(RealProductionMessage));
    }

    [Fact]
    public void The_duplicate_message_yields_the_torrent_id_so_it_can_be_adopted()
    {
        // Deluge's own id is the join key: adopting requires reading it back out of the message.
        Assert.Equal("57a0d12b99fff5d559761b9470785439c1414aa1", DelugeError.HashIn(RealProductionMessage));
    }

    [Theory]
    [InlineData("Not authenticated")]
    [InlineData("Invalid session")]
    [InlineData("Password does not match")]
    public void Real_auth_failures_are_still_recognised(string message) =>
        Assert.Equal(DelugeErrorKind.NotAuthenticated, DelugeError.Classify(message));

    [Theory]
    [InlineData("Unknown torrent")]
    [InlineData("torrent not found")]
    [InlineData("InvalidTorrentError: Invalid torrent id")]
    public void A_vanished_torrent_is_its_own_category(string message) =>
        Assert.Equal(DelugeErrorKind.UnknownTorrent, DelugeError.Classify(message));

    [Theory]
    // The word "session" appearing anywhere must not imply an auth problem — that was the whole bug.
    [InlineData("Torrent already in session (0123456789abcdef0123456789abcdef01234567)", DelugeErrorKind.AlreadyInSession)]
    // ...nor may an incidental "auth" in a torrent name or tracker message.
    [InlineData("Deluge RPC failed: tracker rejected: unauthorized announce", DelugeErrorKind.Unknown)]
    [InlineData("Added Authority.S01E01.1080p", DelugeErrorKind.Unknown)]
    [InlineData("", DelugeErrorKind.Unknown)]
    [InlineData(null, DelugeErrorKind.Unknown)]
    public void Incidental_keywords_do_not_change_the_category(string? message, DelugeErrorKind expected) =>
        Assert.Equal(expected, DelugeError.Classify(message));

    [Theory]
    [InlineData("Torrent already in session (0123456789ABCDEF0123456789abcdef01234567)", "0123456789abcdef0123456789abcdef01234567")]
    // v2 torrents use a 64-hex id, which is exactly why we take Deluge's id rather than computing our own
    // v1 infohash from the magnet — the two would not match and the join would silently drift.
    [InlineData("Torrent already in session (0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef)",
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("Torrent already in session", null)]
    [InlineData("no hash here (not-a-hash)", null)]
    public void Hash_extraction_handles_v1_v2_and_absence(string message, string? expected) =>
        Assert.Equal(expected, DelugeError.HashIn(message));
}
