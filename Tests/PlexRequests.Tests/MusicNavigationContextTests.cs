using PlexRequestsHosted.Shared.Navigation;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public sealed class MusicNavigationContextTests
{
    [Theory]
    [InlineData("/music")]
    [InlineData("/music/category?token=abc&name=Chill")]
    [InlineData("/music/playlist?id=playlist")]
    [InlineData("/media/music/album/musicbrainz/id?returnUrl=%2Fmusic")]
    public void ValidateReturnUrl_AllowsOnlyMusicNavigation(string value)
        => Assert.Equal(value, MusicNavigationContext.ValidateReturnUrl(value));

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("//example.com/music")]
    [InlineData("/admin")]
    [InlineData("/music\\..\\admin")]
    [InlineData("javascript:alert(1)")]
    public void ValidateReturnUrl_RejectsExternalOrUnrelatedTargets(string value)
        => Assert.Equal("/music", MusicNavigationContext.ValidateReturnUrl(value));

    [Fact]
    public void AddReturnContext_EncodesNestedPathAndLabel()
    {
        var result = MusicNavigationContext.AddReturnContext(
            "/media/music/track/youtube/id", "/music/category?name=R&B", "R&B / Soul");

        Assert.Equal(
            "/media/music/track/youtube/id?returnUrl=%2Fmusic%2Fcategory%3Fname%3DR%26B&returnLabel=R%26B%20%2F%20Soul",
            result);
    }

    [Fact]
    public void AddReturnContext_AppendsToAnExistingQuery()
    {
        var result = MusicNavigationContext.AddReturnContext(
            "/music/playlist?id=playlist", "/music/category?name=Chill", "Chill");

        Assert.StartsWith("/music/playlist?id=playlist&returnUrl=", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveReturnLabel_DoesNotHonorALabelForARejectedTarget()
        => Assert.Equal("Music", MusicNavigationContext.ResolveReturnLabel("https://example.com", "Example"));

    [Theory]
    [InlineData(null, null, "/music")]
    [InlineData("Daft Punk", null, "/music?q=Daft%20Punk")]
    [InlineData(" R&B ", MediaKind.Track, "/music?q=R%26B&kind=track")]
    public void BuildSearchUrl_PreservesDedicatedMusicSearch(string? query, MediaKind? kind, string expected)
        => Assert.Equal(expected, MusicNavigationContext.BuildSearchUrl(query, kind));
}
