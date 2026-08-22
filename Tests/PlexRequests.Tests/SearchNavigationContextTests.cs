using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Navigation;
using Xunit;

namespace PlexRequests.Tests;

public sealed class SearchNavigationContextTests
{
    [Theory]
    [InlineData("movie", MediaType.Movie)]
    [InlineData("movies", MediaType.Movie)]
    [InlineData("tv", MediaType.TvShow)]
    [InlineData("tvshow", MediaType.TvShow)]
    [InlineData("TV Shows", MediaType.TvShow)]
    [InlineData("series", MediaType.TvShow)]
    [InlineData("anime", MediaType.Anime)]
    [InlineData("music", MediaType.Music)]
    public void Known_route_tokens_select_one_media_type(string token, MediaType expected)
        => Assert.Equal(expected, SearchNavigationContext.ParseMediaType(token));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("all")]
    [InlineData("podcast")]
    public void Missing_or_unknown_route_tokens_keep_generic_search(string? token)
        => Assert.Null(SearchNavigationContext.ParseMediaType(token));
}
