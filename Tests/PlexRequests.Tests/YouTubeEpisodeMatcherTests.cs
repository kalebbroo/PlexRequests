using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using Xunit;

namespace PlexRequests.Tests;

public sealed class YouTubeEpisodeMatcherTests
{
    private const string Show = "Marvel's Spidey and His Amazing Friends";
    private const string OfficialChannel = "UCAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly string[] Allowed = [OfficialChannel];

    [Fact]
    public void Accepts_one_upload_named_by_an_exact_season_and_episode_token()
    {
        var outcome = YouTubeEpisodeMatcher.Match(Show, 3, 1, "The Friendly Neighborhood", Allowed,
        [
            Video("abcdefghijk", "Marvel's Spidey and His Amazing Friends | S3 E1 | The Friendly Neighborhood"),
            Video("ZYXWVUT9876", "Marvel's Spidey and His Amazing Friends | S3 E2 | Another Episode")
        ]);

        Assert.Equal("abcdefghijk", outcome.Selected?.VideoId);
    }

    [Fact]
    public void Accepts_the_show_when_the_upload_drops_the_marvel_branding()
    {
        var outcome = YouTubeEpisodeMatcher.Match(Show, 3, 1, null, Allowed,
            [Video("abcdefghijk", "Spidey and His Amazing Friends S3E1 Full Episode")]);

        Assert.Equal("abcdefghijk", outcome.Selected?.VideoId);
    }

    [Fact]
    public void Accepts_an_exact_episode_title_when_the_upload_has_no_token()
    {
        var outcome = YouTubeEpisodeMatcher.Match(Show, 3, 1, "The Friendly Neighborhood", Allowed,
        [
            Video("abcdefghijk", "Marvel's Spidey and His Amazing Friends | The Friendly Neighborhood | Full Episode")
        ]);

        Assert.Equal("abcdefghijk", outcome.Selected?.VideoId);
    }

    [Fact]
    public void Refuses_a_title_match_when_the_upload_names_a_different_episode()
    {
        var outcome = YouTubeEpisodeMatcher.Match(Show, 3, 1, "The Friendly Neighborhood", Allowed,
        [
            Video("abcdefghijk", "Marvel's Spidey and His Amazing Friends | S3 E2 | The Friendly Neighborhood")
        ]);

        Assert.Null(outcome.Selected);
        Assert.Contains(outcome.Candidates, x => x.Reasons.Any(r => r.Contains("different season or episode")));
    }

    [Fact]
    public void Two_valid_uploads_are_ambiguous_and_select_nothing()
    {
        var outcome = YouTubeEpisodeMatcher.Match(Show, 3, 1, null, Allowed,
        [
            Video("abcdefghijk", "Marvel's Spidey and His Amazing Friends S3E1 Full Episode"),
            Video("ZYXWVUT9876", "Marvel's Spidey and His Amazing Friends S3E1 Full Episode (repost)")
        ]);

        Assert.Null(outcome.Selected);
        Assert.Equal(2, outcome.Candidates.Count(x => x.Accepted));
    }

    [Theory]
    [InlineData("Marvel's Spidey and His Amazing Friends S3E1 Clips", "clip")]
    [InlineData("Marvel's Spidey and His Amazing Friends S3E1 Compilation", "compilation")]
    [InlineData("Marvel's Spidey and His Amazing Friends S3E1 Part 2", "multi-part")]
    [InlineData("Marvel's Spidey and His Amazing Friends Episodes 1-2 S3E1", "range")]
    [InlineData("PAW Patrol Season 3 Episode 1 Full Episode", "other show")]
    public void Rejects_clips_compilations_parts_ranges_and_other_shows(string title, string _)
    {
        var outcome = YouTubeEpisodeMatcher.Match(Show, 3, 1, null, Allowed, [Video("abcdefghijk", title)]);

        Assert.Null(outcome.Selected);
    }

    [Fact]
    public void Rejects_uploads_from_channels_that_are_not_allowlisted()
    {
        var outcome = YouTubeEpisodeMatcher.Match(Show, 3, 1, null, Allowed,
        [
            Video("abcdefghijk", "Marvel's Spidey and His Amazing Friends S3E1", channel: "UCBBBBBBBBBBBBBBBBBBBBBB")
        ]);

        Assert.Null(outcome.Selected);
        Assert.Contains(outcome.Candidates, x => x.Reasons.Any(r => r.Contains("allowlist")));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(3000)]
    public void Rejects_runtimes_outside_the_episode_window(int seconds)
    {
        var outcome = YouTubeEpisodeMatcher.Match(Show, 3, 1, null, Allowed,
            [Video("abcdefghijk", "Marvel's Spidey and His Amazing Friends S3E1", seconds: seconds)]);

        Assert.Null(outcome.Selected);
    }

    [Fact]
    public void Two_digit_episode_does_not_satisfy_a_one_digit_target()
    {
        var outcome = YouTubeEpisodeMatcher.Match(Show, 3, 1, null, Allowed,
            [Video("abcdefghijk", "Marvel's Spidey and His Amazing Friends S3E10")]);

        Assert.Null(outcome.Selected);
    }

    [Fact]
    public void Live_streams_are_never_matched()
    {
        var outcome = YouTubeEpisodeMatcher.Match(Show, 3, 1, null, Allowed,
            [Video("abcdefghijk", "Marvel's Spidey and His Amazing Friends S3E1", live: true)]);

        Assert.Null(outcome.Selected);
    }

    [Fact]
    public void Normalization_treats_an_apostrophe_as_a_joined_word()
    {
        Assert.Equal(YouTubeEpisodeMatcher.Normalize("Marvel's Spidey"), YouTubeEpisodeMatcher.Normalize("Marvels Spidey"));
        Assert.Equal("ghost spider", YouTubeEpisodeMatcher.Normalize("Ghost-Spider!"));
    }

    private static YouTubeCatalogVideo Video(string id, string title, string channel = OfficialChannel,
        int seconds = 660, bool live = false) => new(id, channel, title, seconds, live);
}
