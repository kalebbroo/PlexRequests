using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Organize;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using Xunit;

namespace PlexRequests.Tests;

public class LibraryRoutingTests
{
    [Fact]
    public void LegacyPathsAndInlineRules_AreUpgradedWithoutChangingTargets()
    {
        var prefs = new LibraryOrganizationPreferencesDto
        {
            MoviePath = "/media/movies",
            TvPath = "/media/tv",
            MusicPath = "/media/music",
            LibraryRootRules =
            [
                new LibraryRootRuleDto
                {
                    MediaType = MediaType.TvShow,
                    RequireAnime = true,
                    RootPath = "/media/anime-tv"
                }
            ]
        };

        LibraryRouting.EnsureDestinationModel(prefs);

        Assert.Equal(4, prefs.LibraryDestinations.Count);
        Assert.Equal("/media/movies", prefs.LibraryDestinations.Single(d =>
            d.ContentKind == LibraryContentKind.Movie && d.IsDefault).RootPath);
        Assert.False(string.IsNullOrWhiteSpace(prefs.LibraryRootRules[0].DestinationId));
        var anime = LibraryRouting.Resolve(prefs, MediaType.TvShow, Quality.FullHD,
            ["Animation"], isAnime: true, isEpisode: true);
        Assert.Equal("/media/anime-tv", anime.RootPath);
    }

    [Fact]
    public void AnimeMoviesAnimeSeriesAndKidsSeries_RouteToDifferentCompatibleDestinations()
    {
        var prefs = Preferences();

        var animeMovie = LibraryRouting.Resolve(prefs, MediaType.Movie, Quality.FullHD,
            ["Animation"], isAnime: true, isEpisode: false);
        var animeSeries = LibraryRouting.Resolve(prefs, MediaType.TvShow, Quality.FullHD,
            ["Animation"], isAnime: true, isEpisode: true);
        var kidsSeries = LibraryRouting.Resolve(prefs, MediaType.TvShow, Quality.HD,
            ["Animation", "Kids"], isAnime: false, isEpisode: true);

        Assert.Equal("anime-movies", animeMovie.Id);
        Assert.Equal("anime-tv", animeSeries.Id);
        Assert.Equal("kids-tv", kidsSeries.Id);
        Assert.Equal(LibraryContentKind.Movie, animeMovie.ContentKind);
        Assert.Equal(LibraryContentKind.Series, animeSeries.ContentKind);
    }

    [Fact]
    public void ValidationRejectsRuleThatTargetsWrongScannerShape()
    {
        var prefs = Preferences();
        prefs.LibraryRootRules[0].DestinationId = "anime-tv";

        Assert.False(LibraryRouting.TryNormalizeAndValidate(prefs, out var error));
        Assert.Contains("cannot target", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyAnimeRule_DoesNotCaptureOrdinaryTv()
    {
        var prefs = Preferences();
        prefs.LibraryRootRules =
        [
            new LibraryRootRuleDto
            {
                MediaType = MediaType.Anime,
                DestinationId = "anime-tv"
            }
        ];

        var ordinaryTv = LibraryRouting.Resolve(prefs, MediaType.TvShow, Quality.FullHD,
            ["Drama"], isAnime: false, isEpisode: true);
        var anime = LibraryRouting.Resolve(prefs, MediaType.TvShow, Quality.FullHD,
            ["Animation"], isAnime: true, isEpisode: true);

        Assert.Equal("tv", ordinaryTv.Id);
        Assert.Equal("anime-tv", anime.Id);
    }

    [Fact]
    public void NamingUsesImmutableSnapshotAndRejectsPathEscape()
    {
        var naming = new PlexNamingService();
        var prefs = new EffectiveLibraryOrganization
        {
            MoviePath = "/changed/default",
            MovieTemplate = "{Title}/{Title}{Ext}"
        };
        var job = new FulfillmentJobDto
        {
            MediaType = MediaType.Movie,
            Title = "Akira",
            Year = 1988,
            LibraryDestination = new LibraryDestinationSnapshotDto
            {
                Id = "anime-movies",
                Name = "Anime Movies",
                ContentKind = LibraryContentKind.Movie,
                RootPath = "/library/anime-movies",
                Template = "{Title} ({Year})/{Title}{Ext}"
            }
        };

        Assert.Equal("/library/anime-movies/Akira (1988)/Akira.mkv",
            naming.BuildMoviePath(prefs, job, ".mkv"));

        job.LibraryDestination.Template = "../../outside/{Title}{Ext}";
        var ex = Assert.Throws<InvalidOperationException>(() => naming.BuildMoviePath(prefs, job, ".mkv"));
        Assert.Contains("escapes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static LibraryOrganizationPreferencesDto Preferences() => new()
    {
        MoviePath = "/media/movies",
        TvPath = "/media/tv",
        MusicPath = "/media/music",
        LibraryDestinations =
        [
            Destination("movies", "Movies", LibraryContentKind.Movie, "/media/movies", isDefault: true),
            Destination("tv", "TV", LibraryContentKind.Series, "/media/tv", isDefault: true),
            Destination("music", "Music", LibraryContentKind.Music, "/media/music", isDefault: true),
            Destination("anime-movies", "Anime Movies", LibraryContentKind.Movie, "/media/anime-movies"),
            Destination("anime-tv", "Anime TV", LibraryContentKind.Series, "/media/anime-tv"),
            Destination("kids-tv", "Kids TV", LibraryContentKind.Series, "/media/kids-tv")
        ],
        LibraryRootRules =
        [
            new LibraryRootRuleDto
                { MediaType = MediaType.Movie, RequireAnime = true, DestinationId = "anime-movies" },
            new LibraryRootRuleDto
                { MediaType = MediaType.TvShow, RequireAnime = true, DestinationId = "anime-tv" },
            new LibraryRootRuleDto
                { MediaType = MediaType.TvShow, RequireAnime = false, GenreContains = "Kids", DestinationId = "kids-tv" }
        ]
    };

    private static LibraryDestinationDto Destination(string id, string name, LibraryContentKind kind,
        string root, bool isDefault = false) => new()
    {
        Id = id,
        Name = name,
        ContentKind = kind,
        RootPath = root,
        IsDefault = isDefault,
        Enabled = true
    };
}
