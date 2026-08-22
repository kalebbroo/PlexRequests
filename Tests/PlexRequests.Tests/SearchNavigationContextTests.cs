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

    [Theory]
    [InlineData(null, null, "/search")]
    [InlineData("  Dune & Spice  ", null, "/search?q=Dune%20%26%20Spice")]
    [InlineData("Severance", MediaType.TvShow, "/search?type=tvshow&q=Severance")]
    [InlineData("Akira", MediaType.Anime, "/search?type=anime&q=Akira")]
    public void Canonical_urls_preserve_query_and_media_filter(
        string? query, MediaType? mediaType, string expected)
        => Assert.Equal(expected, SearchNavigationContext.BuildUrl(query, mediaType));

    [Fact]
    public void Music_url_preserves_kind_source_and_encoded_query()
        => Assert.Equal(
            "/search?type=music&kind=artist&source=youtube%20music&q=Daft%20Punk",
            SearchNavigationContext.BuildUrl("Daft Punk", MediaType.Music, MediaKind.Artist, " youtube music "));

    [Fact]
    public void Non_music_url_drops_music_only_parameters()
        => Assert.Equal(
            "/search?type=movie&q=Heat",
            SearchNavigationContext.BuildUrl("Heat", MediaType.Movie, MediaKind.Track, "youtube"));
}
