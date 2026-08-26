using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.Enums;
using TMDbLib.Objects.TvShows;
using Xunit;

namespace PlexRequests.Tests;

public sealed class TmdbEpisodeGroupImportTests
{
    private static readonly DateTime ImportedAt = new(2026, 8, 26, 2, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AbsoluteGroupFlattensMainEpisodesAndSkipsSpecials()
    {
        var details = Group(TvGroupType.Absolute,
            Season("Specials", 0, Episode(42, 0, 1, 0)),
            Season("Series", 1, Episode(42, 1, 1, 0), Episode(42, 1, 2, 2)));

        var preview = TmdbEpisodeGroupImportService.BuildPreview(42, "Anime", details, ImportedAt);

        Assert.Equal(EpisodeOrderType.Absolute, preview.Profile.SourceOrder);
        Assert.Equal("A1 -> S01E01\nA3 -> S01E02", preview.Profile.MappingsText);
        Assert.Equal(3, preview.SourceEpisodeCount);
        Assert.Equal(2, preview.MappedEpisodeCount);
        Assert.Equal(1, preview.SkippedSpecialCount);
        Assert.Contains("special", preview.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("group-id", preview.Profile.SourceEpisodeGroupId);
        Assert.Equal(ImportedAt, preview.Profile.ImportedAt);
    }

    [Fact]
    public void DigitalGroupUsesOneBasedGroupAndEpisodeOrder()
    {
        var details = Group(TvGroupType.Digital,
            Season("Volume 1", 1, Episode(42, 1, 5, 0), Episode(42, 1, 6, 1)),
            Season("Volume 2", 2, Episode(42, 2, 1, 0)));

        var preview = TmdbEpisodeGroupImportService.BuildPreview(42, "Kids Show", details, ImportedAt);

        Assert.Equal(EpisodeOrderType.Digital, preview.Profile.SourceOrder);
        Assert.Equal(
            "S01E01 -> S01E05\nS01E02 -> S01E06\nS02E01 -> S02E01",
            preview.Profile.MappingsText);
        Assert.Equal(3, preview.MappedEpisodeCount);
        Assert.Null(preview.Warning);
    }

    [Fact]
    public void OriginalAirDateAlternateGroupRemainsAnExplicitCustomTranslation()
    {
        var details = Group(TvGroupType.OriginalAirDate,
            Season("TVDB Season", 1, Episode(42, 2, 8, 0)));

        var preview = TmdbEpisodeGroupImportService.BuildPreview(42, "Kids Show", details, ImportedAt);

        Assert.Equal(EpisodeOrderType.Custom, preview.Profile.SourceOrder);
        Assert.Equal("S01E01 -> S02E08", preview.Profile.MappingsText);
    }

    [Fact]
    public void GroupForDifferentSeriesIsRejected()
    {
        var details = Group(TvGroupType.DVD, Season("Disc 1", 1, Episode(99, 1, 1, 0)));

        var ex = Assert.Throws<ArgumentException>(() =>
            TmdbEpisodeGroupImportService.BuildPreview(42, "Wrong Show", details, ImportedAt));

        Assert.Contains("different TMDb series", ex.Message);
    }

    [Fact]
    public void DuplicateGeneratedSourceIdentityIsRejected()
    {
        var details = Group(TvGroupType.DVD,
            Season("Disc 1", 1, Episode(42, 1, 1, 0)),
            Season("Duplicate Disc", 1, Episode(42, 1, 2, 0)));

        var ex = Assert.Throws<ArgumentException>(() =>
            TmdbEpisodeGroupImportService.BuildPreview(42, "Unsafe", details, ImportedAt));

        Assert.Contains("unsafe episode map", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static TvGroupCollection Group(TvGroupType type, params TvGroup[] groups) => new()
    {
        Id = "group-id",
        Name = "Selected order",
        Type = type,
        EpisodeCount = groups.Sum(x => x.Episodes.Count),
        GroupCount = groups.Length,
        Groups = groups.ToList()
    };

    private static TvGroup Season(string name, int order, params TvGroupEpisode[] episodes) => new()
    {
        Id = $"group-{order}-{name}",
        Name = name,
        Order = order,
        Episodes = episodes.ToList()
    };

    private static TvGroupEpisode Episode(int showId, int season, int episode, int order) => new()
    {
        ShowId = showId,
        SeasonNumber = season,
        EpisodeNumber = episode,
        Order = order,
        Name = $"S{season}E{episode}"
    };
}
