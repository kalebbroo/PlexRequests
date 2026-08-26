using Microsoft.Extensions.Logging.Abstractions;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Organize;
using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using Xunit;

namespace PlexRequests.Tests;

public sealed class MediaLanguagePolicyTests
{
    [Fact]
    public void DualAudio_RequiresEnglishAndJapaneseOnTheSameFile()
    {
        var policy = MediaLanguagePolicy.FromProfile(new QualityProfileDto
        {
            RequiredAudioLanguagesCsv = "eng,jpn",
            AllowUnknownTrackLanguage = false
        });

        var rejected = MediaLanguagePolicy.Evaluate(policy, Tracks(audio: ["ja"]));
        var accepted = MediaLanguagePolicy.Evaluate(policy, Tracks(audio: ["eng", "jpn"]));

        Assert.False(rejected.Accepted);
        Assert.Contains("en", rejected.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(accepted.Accepted);
    }

    [Fact]
    public void JapaneseWithEnglishSubtitles_RejectsMissingOrUntaggedSubtitle()
    {
        var policy = MediaLanguagePolicy.FromProfile(new QualityProfileDto
        {
            RequiredAudioLanguagesCsv = "ja",
            RequiredSubtitleLanguagesCsv = "en",
            AllowUnknownTrackLanguage = false
        });

        Assert.False(MediaLanguagePolicy.Evaluate(policy, Tracks(audio: ["ja"], subtitles: [null])).Accepted);
        Assert.True(MediaLanguagePolicy.Evaluate(policy, Tracks(audio: ["ja"], subtitles: ["eng"])).Accepted);
    }

    [Theory]
    [InlineData("rus", "ru")]
    [InlineData("nld", "nl")]
    [InlineData("tam", "ta")]
    [InlineData("tel", "te")]
    public void MediaInfo_language_codes_normalize_to_profile_codes(string observed, string expected)
    {
        Assert.Equal(expected, MediaLanguagePolicy.Normalize(observed));
        Assert.Equal(expected, MediaLanguagePolicy.NormalizeExplicitLanguage(observed));
    }

    [Fact]
    public void ForcedSubtitleRequirement_UsesObservedDisposition()
    {
        var policy = new MediaLanguagePolicyDto { RequireForcedSubtitle = true };
        var tracks = Tracks(subtitles: ["en"]);

        Assert.False(MediaLanguagePolicy.Evaluate(policy, tracks).Accepted);
        tracks.Subtitles[0].IsForced = true;
        Assert.True(MediaLanguagePolicy.Evaluate(policy, tracks).Accepted);
    }

    [Fact]
    public void MediaInfoJson_SeparatesAudioSubtitleDispositionAndSidecars()
    {
        const string json = """
            {"media":{"track":[
              {"@type":"General","Format":"Matroska"},
              {"@type":"Audio","Format":"AAC","Language":"jpn","Title":"Main","Default":"Yes","Forced":"No"},
              {"@type":"Text","Format":"ASS","Language":"eng","Default":"No","Forced":"Yes"}
            ]}}
            """;

        var result = MediaInfoTrackInspector.ParseOutput(json,
            ["/downloads/Show.S01E01.ja.forced.srt"]);

        var audio = Assert.Single(result.Audio);
        Assert.Equal("ja", audio.Language);
        Assert.True(audio.IsDefault);
        Assert.Contains(result.Subtitles, x => x.Language == "en" && x.IsForced && !x.IsExternal);
        Assert.Contains(result.Subtitles, x => x.Language == "ja" && x.IsForced && x.IsExternal);
    }

    [Fact]
    public void ProfileAssignment_MatchesAnimeAsClassificationWithoutLosingMovieTvShape()
    {
        var animeMovies = new ProfileAssignmentRuleEntity
            { MatchMediaType = MediaType.Movie, MatchAnime = true };
        var legacyAnimeTv = new ProfileAssignmentRuleEntity { MatchMediaType = MediaType.Anime };

        Assert.True(QualityProfileSeeder.Matches(animeMovies, MediaType.Movie, 1, [], isAnime: true));
        Assert.False(QualityProfileSeeder.Matches(animeMovies, MediaType.TvShow, 1, [], isAnime: true));
        Assert.True(QualityProfileSeeder.Matches(legacyAnimeTv, MediaType.TvShow, 1, [], isAnime: true));
        Assert.False(QualityProfileSeeder.Matches(legacyAnimeTv, MediaType.TvShow, 1, [], isAnime: false));
        Assert.False(QualityProfileSeeder.Matches(
            new ProfileAssignmentRuleEntity { MatchAnime = false }, MediaType.TvShow, 1, [], isAnime: null));
    }

    [Fact]
    public async Task OrganizerRejectsMismatchBeforeWritingToLibrary()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source.mkv");
            await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
            var library = Path.Combine(root, "library");
            var inspector = new FixedInspector(Tracks(audio: ["ja"]));
            var organizer = CreateOrganizer(inspector);

            var result = await organizer.OrganizeAsync(MovieJob(library),
                new TransferItem("transfer", null, null, false), source, Preferences(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(BlocklistReason.MediaPolicyMismatch, result.BlocklistReason);
            Assert.False(Directory.Exists(library));
            Assert.Equal(1, inspector.Calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OrganizerPersistsObservedTracksOnAcceptedVideoRecord()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source.mkv");
            await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
            var library = Path.Combine(root, "library");
            var observed = Tracks(audio: ["en", "ja"], subtitles: ["en"]);
            var organizer = CreateOrganizer(new FixedInspector(observed));

            var result = await organizer.OrganizeAsync(MovieJob(library),
                new TransferItem("transfer", null, null, false), source, Preferences(), CancellationToken.None);

            Assert.True(result.Success, result.FailReason);
            var video = Assert.Single(result.Files, x => x.FileType == "video");
            Assert.Same(observed, video.MediaTracks);
            Assert.True(File.Exists(video.DestinationPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static LibraryOrganizer CreateOrganizer(IMediaTrackInspector inspector) => new(
        new NoArchives(), new NoSeasonPacks(), new NoEpisodes(), new PlexNamingService(),
        new ReleaseParser(), inspector, NullLogger<LibraryOrganizer>.Instance);

    private static EffectiveLibraryOrganization Preferences() => new()
    {
        TransferMode = TransferMode.Copy,
        MinVideoFileSizeMb = 0
    };

    private static FulfillmentJobDto MovieJob(string library) => new()
    {
        Id = 10,
        Title = "Anime Movie",
        Year = 2026,
        MediaType = MediaType.Movie,
        QualityProfile = new QualityProfileDto { Name = "Dual Audio" },
        MediaLanguagePolicy = new MediaLanguagePolicyDto
        {
            RequiredAudioLanguages = ["en", "ja"],
            AllowUnknownTrackLanguage = false
        },
        LibraryDestination = new LibraryDestinationSnapshotDto
        {
            Id = "anime-movies",
            Name = "Anime Movies",
            ContentKind = LibraryContentKind.Movie,
            RootPath = library,
            Template = "{Title} ({Year})/{Title}{Ext}"
        }
    };

    private static MediaTrackSummaryDto Tracks(IEnumerable<string?>? audio = null,
        IEnumerable<string?>? subtitles = null) => new()
    {
        Audio = (audio ?? []).Select(language => new MediaTrackDto
            { Type = "audio", Language = language }).ToList(),
        Subtitles = (subtitles ?? []).Select(language => new MediaTrackDto
            { Type = "subtitle", Language = language }).ToList()
    };

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plexrequests-language-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedInspector(MediaTrackSummaryDto tracks) : IMediaTrackInspector
    {
        public int Calls { get; private set; }
        public Task<MediaTrackSummaryDto> InspectAsync(string mediaPath,
            IReadOnlyList<string> companionSubtitles, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(tracks);
        }
    }

    private sealed class NoArchives : IArchiveExtractor
    {
        public bool LooksLikeArchive(string filePath) => false;
        public bool IsContinuationVolume(string filePath) => false;
        public Task ExtractAsync(string archiveFilePath, string destinationDirectory, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class NoSeasonPacks : ISeasonPackSplitter
    {
        public SeasonPackMapResult Map(IReadOnlyList<string> videoFiles,
            int season, int? expectedEpisodeCount) => new([], [], []);
    }

    private sealed class NoEpisodes : IEpisodeTitleProvider
    {
        public Task<IReadOnlyList<EpisodeDto>> GetSeasonEpisodesAsync(int? tmdbId, int season,
            CancellationToken ct) => Task.FromResult<IReadOnlyList<EpisodeDto>>([]);
        public Task<string?> GetEpisodeTitleAsync(int? tmdbId, int season, int episode,
            CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
