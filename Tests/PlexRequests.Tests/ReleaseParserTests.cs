using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

public class ReleaseParserTests
{
    private readonly ReleaseParser _parser = new();

    [Theory]
    [InlineData("Severance.S02E07.1080p.WEB-DL.DDP5.1.H.264-NTb", 1080)]
    [InlineData("Dune.Part.Two.2024.2160p.UHD.BluRay.x265-GROUP", 2160)]
    [InlineData("Some.Show.S01E01.720p.HDTV.x264", 720)]
    [InlineData("Old.Movie.1998.480p.DVDRip", 480)]
    [InlineData("Show.Name.S01E01-GROUP", 0)] // no resolution token at all
    public void Parses_resolution(string name, int expected) =>
        Assert.Equal(expected, _parser.Parse(name).Resolution);

    [Theory]
    [InlineData("Show.S01E01.1080p.WEB-DL-GRP", ReleaseSource.WebDl)]
    [InlineData("Show.S01E01.1080p.BluRay-GRP", ReleaseSource.BluRay)]
    [InlineData("Show.S01E01.1080p.REMUX-GRP", ReleaseSource.Remux)]
    [InlineData("Show.S01E01.1080p.HDTV-GRP", ReleaseSource.Hdtv)]
    [InlineData("Movie.2024.CAM.x264", ReleaseSource.Cam)]
    [InlineData("Show_S01E01_1080p_WEB_DL-GRP", ReleaseSource.WebDl)]
    [InlineData("Show_S01E01_1080p_web-rip-GRP", ReleaseSource.WebRip)]
    [InlineData("Show.S01E01.1080p-GRP", ReleaseSource.Unknown)]
    public void Parses_source(string name, ReleaseSource expected) =>
        Assert.Equal(expected, _parser.Parse(name).Source);

    [Theory]
    [InlineData("Severance.S02E07.1080p", 2, 7)]
    [InlineData("Severance.2x07.1080p", 2, 7)]
    [InlineData("Show_S02_E07_1080p", 2, 7)]
    public void Parses_single_episode(string name, int season, int episode)
    {
        var p = _parser.Parse(name);
        Assert.Equal(season, p.Season);
        Assert.Equal(episode, p.Episode);
        Assert.False(p.IsSeasonPack);
    }

    [Theory]
    [InlineData("Severance.S02.1080p.WEB-DL", 2)]
    [InlineData("Severance.Season.2.1080p", 2)]
    public void Parses_season_pack(string name, int season)
    {
        var p = _parser.Parse(name);
        Assert.Equal(season, p.Season);
        Assert.Null(p.Episode);
        Assert.True(p.IsSeasonPack);
    }

    [Fact]
    public void Parses_multi_season_range()
    {
        var p = _parser.Parse("The.Office.S01-S09.COMPLETE.1080p");
        Assert.Equal(1, p.Season);
        Assert.Equal(9, p.SeasonEnd);
        Assert.True(p.IsSeasonPack);
    }

    // The episode-range form is what lets a partial pack be told apart from a full season. Before this
    // existed, "S01E01-E06" parsed as the single episode S01E01 and a six-episode pack could be accepted
    // for a thirteen-episode season — the request then quietly ended up incomplete.
    [Theory]
    [InlineData("Show.S01E01-E06.1080p.WEB-DL", 1, 1, 6)]
    [InlineData("Show.S02E01-E12.1080p", 2, 1, 12)]
    [InlineData("Show.S01E01-06.720p", 1, 1, 6)]
    [InlineData("Show_S01E01-E06_1080p", 1, 1, 6)]
    public void Parses_episode_range_as_partial_pack(string name, int season, int start, int end)
    {
        var p = _parser.Parse(name);
        Assert.Equal(season, p.Season);
        Assert.True(p.IsSeasonPack);
        Assert.Null(p.Episode);
        Assert.Equal(start, p.EpisodeStart);
        Assert.Equal(end, p.EpisodeEnd);
    }

    [Fact]
    public void Complete_series_is_distinct_from_unparsed()
    {
        Assert.True(_parser.Parse("Show.Name.COMPLETE.SERIES.1080p").LooksLikeCompleteSeries);
        // A name that simply didn't parse a season must NOT be treated as matching every season.
        Assert.False(_parser.Parse("Some.Random.Release.1080p").LooksLikeCompleteSeries);
    }

    [Theory]
    [InlineData("Lucky.Star.S01.1080p-GRP", "Lucky Star")]
    [InlineData("Lucky.2011.1080p.BluRay", "Lucky")]
    [InlineData("The.Office.US.S01.1080p", "The Office US")]
    [InlineData("Severance.S02E07.1080p.WEB-DL", "Severance")]
    [InlineData("Lucky_Star.S01.1080p-GRP", "Lucky Star")]
    [InlineData("Lucky_Star.1080p", "Lucky Star")]
    public void Extracts_core_title(string name, string expected) =>
        Assert.Equal(expected, _parser.Parse(name).Title);

    [Fact]
    public void Parses_proper_repack_and_codec()
    {
        var p = _parser.Parse("Show.S01E01.1080p.WEB-DL.x265.REPACK-GRP");
        Assert.True(p.ProperOrRepack);
        Assert.Equal("x265", p.Codec);
        Assert.Equal("GRP", p.Group);

        var p2 = _parser.Parse("Show.S01E01_1080p_WEB-DL_GRP");
        Assert.Equal("GRP", p2.Group);
    }

    [Theory]
    [InlineData("Daft.Punk-Random.Access.Memories-2013-FLAC-24bit-96kHz", "FLAC", true, 24, 96.0)]
    [InlineData("Artist - Album (2020) ALAC 16-bit 44.1kHz", "ALAC", true, 16, 44.1)]
    [InlineData("Artist.Album.2024.MP3.320", "MP3", false, null, null)]
    public void Parses_music_audio_quality(string name, string codec, bool lossless, int? depth, double? rate)
    {
        var parsed = _parser.Parse(name);
        Assert.Equal(codec, parsed.AudioCodec);
        Assert.Equal(lossless, parsed.LosslessAudio);
        Assert.Equal(depth, parsed.AudioBitDepth);
        Assert.Equal(rate, parsed.AudioSampleRateKhz);
    }
}
