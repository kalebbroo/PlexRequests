using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Tests;

/// <summary>Builders for the fixtures every test needs, so individual tests stay about one behaviour.</summary>
public static class TestData
{
    /// <summary>The seeded tier catalog: every resolution × every source, weighted the same way the app does.</summary>
    public static List<QualityDefinitionDto> Definitions()
    {
        var resolutions = new[] { Quality.SD, Quality.HD, Quality.FullHD, Quality.UHD4K };
        var sources = new[] { ReleaseSource.Unknown, ReleaseSource.Cam, ReleaseSource.Hdtv,
                              ReleaseSource.WebRip, ReleaseSource.WebDl, ReleaseSource.BluRay, ReleaseSource.Remux };
        var list = new List<QualityDefinitionDto>();
        int id = 1;
        foreach (var r in resolutions)
            foreach (var s in sources)
                list.Add(new QualityDefinitionDto
                {
                    Id = id++,
                    Name = $"{s}-{(int)r}p",
                    Resolution = (int)r,
                    Source = s,
                    SortWeight = (int)r * 10 + (int)s
                });
        return list;
    }

    public static int TierId(List<QualityDefinitionDto> defs, Quality res, ReleaseSource src) =>
        defs.First(d => d.Resolution == (int)res && d.Source == src).Id;

    /// <summary>A profile allowing everything from <paramref name="floor"/> up, excluding CAM, cutoff at WEBDL.</summary>
    public static QualityProfileDto Profile(List<QualityDefinitionDto> defs, Quality floor = Quality.HD, Quality cutoff = Quality.FullHD)
    {
        var items = defs
            .Where(d => d.Source != ReleaseSource.Cam)
            .OrderBy(d => d.SortWeight)
            .Select(d => new QualityProfileItemDto
            {
                K = $"q:{d.Id}",
                N = d.Name,
                Allowed = d.Resolution >= (int)floor
            }).ToList();

        return new QualityProfileDto
        {
            Id = 1,
            Name = "Test profile",
            Items = items,
            CutoffQualityDefinitionId = TierId(defs, cutoff, ReleaseSource.WebDl),
            UpgradeAllowed = true
        };
    }

    public static DownloadPreferencesDto Preferences() => new()
    {
        MinSeeders = 1,
        MaxSizeGb = 25,
        MaxSeasonPackSizeGb = 80,
        MinTitleSimilarity = 0.5,
        EnforceQualityFloor = true,
        SeasonPackStrategy = SeasonPackStrategy.PreferPack,
        AllowEpisodeFallback = true,
        MaxEpisodesForFanout = 30,
        RelaxFloorAfterEmptySearches = 3
    };

    public static RankingContext Context(QualityProfileDto? profile = null, bool relax = false,
        DownloadPreferencesDto? prefs = null, List<QualityDefinitionDto>? defs = null,
        IReadOnlySet<string>? blocklist = null)
    {
        var definitions = defs ?? Definitions();
        return new RankingContext
        {
            Preferences = prefs ?? Preferences(),
            Profile = profile,
            Definitions = definitions,
            RelaxQualityFloor = relax,
            BlocklistedHashes = blocklist ?? new HashSet<string>(),
            UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }

    public static FulfillmentJobDto Job(
        string title = "Severance",
        MediaType type = MediaType.TvShow,
        Quality quality = Quality.FullHD,
        int? year = null,
        string? imdbId = null,
        List<SeasonTarget>? seasonTargets = null,
        List<EpisodeRef>? episodes = null,
        bool isUpgrade = false,
        int emptySearches = 0) => new()
        {
            Id = 1,
            MediaRequestId = 1,
            Title = title,
            MediaType = type,
            Quality = quality,
            Year = year,
            ImdbId = imdbId,
            SeasonTargets = seasonTargets ?? new(),
            RequestedEpisodes = episodes ?? new(),
            IsUpgrade = isUpgrade,
            EmptySearchCount = emptySearches
        };

    public static ReleaseCandidate Release(
        string name,
        int seeders = 50,
        double sizeGb = 2.0,
        string? imdbId = null,
        int indexerId = 1,
        bool seedersKnown = true,
        bool sizeKnown = true,
        string? infoHash = null) => new()
        {
            ReleaseName = name,
            // A real btih is 40 hex chars; a GUID gives only 32, so pad to length.
            Acquisition = AcquisitionResource.Torrent(
                $"magnet:?xt=urn:btih:{infoHash ?? Guid.NewGuid().ToString("N").PadRight(40, '0')}&dn={Uri.EscapeDataString(name)}",
                infoHash),
            ImdbId = imdbId,
            Seeders = seeders,
            SizeBytes = (long)(sizeGb * 1024 * 1024 * 1024),
            SeedersKnown = seedersKnown,
            SizeKnown = sizeKnown,
            IndexerId = indexerId,
            Source = $"Indexer{indexerId}"
        };

    public static SeasonTarget Season(int season, params int[] missing) =>
        new() { Season = season, EpisodeCount = missing.Length, MissingEpisodes = missing.ToList() };

    public static ReleaseEvaluator Evaluator() => new(new ReleaseParser());
}
