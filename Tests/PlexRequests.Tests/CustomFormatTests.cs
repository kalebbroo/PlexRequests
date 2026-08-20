using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

/// <summary>
/// Extended parsing plus the format matcher. The collision cases are the point: the codec/channel ordering
/// and the whitelisted layouts exist specifically because naive patterns get these wrong.
/// </summary>
public class CustomFormatTests
{
    private readonly ReleaseParser _parser = new();

    // ---- The collisions that dictated the parsing order -------------------------------------------

    [Fact]
    public void H264_is_not_mistaken_for_a_channel_layout()
    {
        // A generic \d\.\d over "H.264" yields "2.6". Channels are whitelisted and matched only after the
        // audio codec has been removed, precisely to stop this.
        var p = _parser.Parse("Show.S01E01.1080p.WEB-DL.H.264-GRP");
        Assert.Null(p.AudioChannels);
    }

    [Fact]
    public void DTS_HD_MA_5_1_yields_both_the_full_codec_and_the_layout()
    {
        // Matching "DTS" before "DTS-HD MA" would call a lossless track lossy, so codecs are longest-first.
        var p = _parser.Parse("Movie.2019.1080p.BluRay.DTS-HD.MA.5.1-GRP");
        Assert.Equal("DTS-HD MA", p.AudioCodec);
        Assert.Equal("5.1", p.AudioChannels);
    }

    [Fact]
    public void Language_tokens_end_the_title_rather_than_becoming_part_of_it()
    {
        // The title boundary is generated from the same token tables the scoring uses, so a language token
        // added for scoring automatically stops the title extractor too.
        var p = _parser.Parse("Movie.2019.MULTi.TrueFrench.DTS-HD.MA.5.1-GRP");
        Assert.Equal("Movie", p.Title);
        Assert.True(p.MultiLanguage);
        Assert.Contains("truefrench", p.Languages);
    }

    [Theory]
    [InlineData("Show.S01E01.2160p.WEB-DL.DV.HDR10.x265", HdrFormat.DolbyVision)]
    [InlineData("Show.S01E01.2160p.WEB-DL.HDR10+.x265", HdrFormat.Hdr10Plus)]
    [InlineData("Show.S01E01.2160p.WEB-DL.HDR10.x265", HdrFormat.Hdr10)]
    [InlineData("Show.S01E01.1080p.WEB-DL.x264", HdrFormat.None)]
    public void Picks_the_most_specific_hdr_variant(string name, HdrFormat expected)
    {
        var p = _parser.Parse(name);
        Assert.Equal(expected, p.HdrFormat);
        // The old boolean is derived, so existing preferences keep working.
        Assert.Equal(expected is HdrFormat.Hdr10 or HdrFormat.Hdr10Plus or HdrFormat.DolbyVision or HdrFormat.Hlg, p.Hdr);
    }

    [Theory]
    [InlineData("Movie.2019.1080p.BluRay.EXTENDED-GRP", "Extended")]
    [InlineData("Movie.2019.1080p.BluRay.Directors.Cut-GRP", "Director's Cut")]
    [InlineData("Movie.2019.1080p.BluRay.IMAX-GRP", "IMAX")]
    [InlineData("Movie.2019.1080p.BluRay-GRP", null)]
    public void Parses_edition(string name, string? expected) =>
        Assert.Equal(expected, _parser.Parse(name).Edition);

    [Fact]
    public void Detects_object_based_audio()
    {
        Assert.True(_parser.Parse("Movie.2019.2160p.TrueHD.Atmos.7.1-GRP").ObjectBasedAudio);
        Assert.False(_parser.Parse("Movie.2019.1080p.AC3.5.1-GRP").ObjectBasedAudio);
    }

    // ---- Matcher semantics -------------------------------------------------------------------------

    private static CustomFormatDto Format(string name, params FormatSpecificationDto[] specs) =>
        new() { Id = 1, Name = name, Enabled = true, Specifications = specs.ToList() };

    private static FormatSpecificationDto Spec(FormatField f, FormatOp op, string v, bool required = false, bool negate = false) =>
        new() { Field = f, Op = op, Value = v, Required = required, Negate = negate };

    private bool Matches(CustomFormatDto format, string releaseName) =>
        CustomFormatMatcher.Matches(format, _parser.Parse(releaseName),
            new ReleaseCandidate { ReleaseName = releaseName, Acquisition = AcquisitionResource.Torrent("") });

    [Fact]
    public void Optional_specs_are_an_any_of_set()
    {
        var f = Format("Lossless",
            Spec(FormatField.AudioCodec, FormatOp.Equals, "DTS-HD MA"),
            Spec(FormatField.AudioCodec, FormatOp.Equals, "TrueHD"));

        Assert.True(Matches(f, "Movie.2019.1080p.BluRay.TrueHD.7.1-GRP"));
        Assert.True(Matches(f, "Movie.2019.1080p.BluRay.DTS-HD.MA.5.1-GRP"));
        Assert.False(Matches(f, "Movie.2019.1080p.BluRay.AC3.5.1-GRP"));
    }

    [Fact]
    public void A_required_spec_must_match_even_when_an_optional_one_does()
    {
        var f = Format("4K lossless",
            Spec(FormatField.Resolution, FormatOp.Equals, "2160", required: true),
            Spec(FormatField.AudioCodec, FormatOp.Equals, "TrueHD"));

        Assert.True(Matches(f, "Movie.2019.2160p.BluRay.TrueHD.7.1-GRP"));
        // Optional matches, required doesn't — no match.
        Assert.False(Matches(f, "Movie.2019.1080p.BluRay.TrueHD.7.1-GRP"));
    }

    [Fact]
    public void Negate_inverts_a_condition()
    {
        var f = Format("Not x265", Spec(FormatField.VideoCodec, FormatOp.Equals, "x265", negate: true));
        Assert.True(Matches(f, "Movie.2019.1080p.BluRay.x264-GRP"));
        Assert.False(Matches(f, "Movie.2019.1080p.BluRay.x265-GRP"));
    }

    [Fact]
    public void Scores_come_from_the_profile_not_the_format()
    {
        var f = Format("x265", Spec(FormatField.VideoCodec, FormatOp.Equals, "x265"));
        var parsed = _parser.Parse("Movie.2019.1080p.BluRay.x265-GRP");
        var candidate = new ReleaseCandidate
        {
            ReleaseName = "Movie.2019.1080p.BluRay.x265-GRP",
            Acquisition = AcquisitionResource.Torrent("")
        };

        var (liked, matched) = CustomFormatMatcher.Score(parsed, candidate, new[] { f }, new Dictionary<int, int> { [1] = 100 });
        var (disliked, _) = CustomFormatMatcher.Score(parsed, candidate, new[] { f }, new Dictionary<int, int> { [1] = -500 });

        Assert.Equal(100, liked);
        Assert.Equal(-500, disliked);
        Assert.Equal(new[] { "x265" }, matched);
    }

    [Fact]
    public void A_disabled_format_never_scores()
    {
        var f = Format("x265", Spec(FormatField.VideoCodec, FormatOp.Equals, "x265"));
        f.Enabled = false;
        var (score, matched) = CustomFormatMatcher.Score(
            _parser.Parse("Movie.2019.1080p.x265-GRP"),
            new ReleaseCandidate
            {
                ReleaseName = "Movie.2019.1080p.x265-GRP",
                Acquisition = AcquisitionResource.Torrent("")
            },
            new[] { f }, new Dictionary<int, int> { [1] = 100 });
        Assert.Equal(0, score);
        Assert.Empty(matched);
    }

    // ---- Regex safety ------------------------------------------------------------------------------

    [Fact]
    public void A_catastrophic_pattern_cannot_hang_the_ranking_loop()
    {
        // (a+)+$ against a long non-matching input is the classic backtracking bomb. Either the pattern
        // compiles as non-backtracking (linear) or it runs under a hard timeout — either way it returns.
        var f = Format("Bomb", Spec(FormatField.ReleaseTitle, FormatOp.Regex, "(a+)+$"));
        var name = new string('a', 40) + "!";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ = Matches(f, name);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000, $"pattern took {sw.ElapsedMilliseconds}ms");
    }

    [Theory]
    [InlineData(@"\b(valid|pattern)\b", true)]
    [InlineData("([unclosed", false)]
    [InlineData("", false)]
    public void Invalid_patterns_are_rejected_at_save_time(string pattern, bool expected) =>
        Assert.Equal(expected, CustomFormatMatcher.IsValidPattern(pattern, out _));

    [Fact]
    public void An_over_long_pattern_is_rejected()
    {
        var huge = new string('a', CustomFormatMatcher.MaxPatternLength + 1);
        Assert.False(CustomFormatMatcher.IsValidPattern(huge, out var error));
        Assert.Contains("limited", error!);
    }
}
