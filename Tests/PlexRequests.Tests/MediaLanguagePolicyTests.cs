using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
    public async Task Seeder_CollapsesLegacyStockChoicesWithoutBreakingHistoricalProfiles()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new PooledDbContextFactory<PlexRequestsHosted.Infrastructure.Data.AppDbContext>(
            new DbContextOptionsBuilder<PlexRequestsHosted.Infrastructure.Data.AppDbContext>()
                .UseSqlite(connection).Options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.QualityProfiles.AddRange(
                new QualityProfileEntity { Name = "Any", IsDefault = true, IsUserSelectable = true },
                new QualityProfileEntity { Name = "HD-1080p", IsUserSelectable = true },
                new QualityProfileEntity { Name = "Ultra-HD", IsUserSelectable = true });
            db.QualityRules.Add(new QualityRuleEntity
            {
                Name = "Legacy default",
                IsDefault = true,
                TargetQuality = Quality.Any
            });
            await db.SaveChangesAsync();
        }

        await new QualityProfileSeeder(factory, NullLogger<QualityProfileSeeder>.Instance).SeedAsync();

        await using var verify = await factory.CreateDbContextAsync();
        var profiles = await verify.QualityProfiles.OrderBy(x => x.Id).ToListAsync();
        var everyday = Assert.Single(profiles, x => x.Name == "Everyday");
        Assert.True(everyday.IsDefault);
        Assert.True(everyday.IsUserSelectable);
        Assert.Equal(ReleaseLanguagePreference.Smart, everyday.LanguagePreference);
        Assert.Equal("en", everyday.PreferredAudioLanguage);
        Assert.DoesNotContain(profiles, x => x.Name == "Unknown (imported)");
        Assert.All(profiles.Where(x => x.Name is "HD-1080p" or "Ultra-HD"),
            profile => Assert.False(profile.IsUserSelectable));
    }

    [Fact]
    public async Task Seeder_RepairsOrphanImportedAnyDefaultFromInterruptedLegacyUpgrade()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new PooledDbContextFactory<PlexRequestsHosted.Infrastructure.Data.AppDbContext>(
            new DbContextOptionsBuilder<PlexRequestsHosted.Infrastructure.Data.AppDbContext>()
                .UseSqlite(connection).Options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.QualityProfiles.AddRange(
                new QualityProfileEntity { Name = "Everyday", IsDefault = false, IsUserSelectable = true },
                new QualityProfileEntity { Name = "Unknown (imported)", IsDefault = true, IsUserSelectable = true });
            await db.SaveChangesAsync();
        }

        await new QualityProfileSeeder(factory, NullLogger<QualityProfileSeeder>.Instance).SeedAsync();

        await using var verify = await factory.CreateDbContextAsync();
        var everyday = await verify.QualityProfiles.SingleAsync(x => x.Name == "Everyday");
        var orphan = await verify.QualityProfiles.SingleAsync(x => x.Name == "Unknown (imported)");
        Assert.True(everyday.IsDefault);
        Assert.False(orphan.IsDefault);
        Assert.False(orphan.IsUserSelectable);
    }

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
    public void SmartPreference_AllowsOriginalLanguageFallback()
    {
        var policy = new MediaLanguagePolicyDto
        {
            Preference = ReleaseLanguagePreference.Smart,
            PreferredAudioLanguage = "en",
            PreferredSubtitleLanguage = "en"
        };

        var result = MediaLanguagePolicy.Evaluate(policy, Tracks(audio: ["ja"], subtitles: ["en"]));

        Assert.True(result.Accepted);
    }

    [Fact]
    public void EnglishOnly_IsARealInspectedTrackRequirement()
    {
        var policy = new MediaLanguagePolicyDto
        {
            Preference = ReleaseLanguagePreference.EnglishOnly,
            PreferredAudioLanguage = "en"
        };

        Assert.False(MediaLanguagePolicy.Evaluate(policy, Tracks(audio: ["it"])).Accepted);
        Assert.True(MediaLanguagePolicy.Evaluate(policy, Tracks(audio: ["it", "eng"])).Accepted);
    }

    [Fact]
    public void OriginalAudioMode_RequiresPreferredSubtitles()
    {
        var policy = new MediaLanguagePolicyDto
        {
            Preference = ReleaseLanguagePreference.OriginalWithEnglishSubtitles,
            PreferredSubtitleLanguage = "en"
        };

        Assert.False(MediaLanguagePolicy.Evaluate(policy, Tracks(audio: ["it"])).Accepted);
        Assert.True(MediaLanguagePolicy.Evaluate(policy, Tracks(audio: ["it"], subtitles: ["eng"])).Accepted);
    }

    [Fact]
    public void MediaInfoJson_SeparatesAudioSubtitleDispositionAndSidecars()
    {
        const string json = """
            {"media":{"track":[
              {"@type":"General","Format":"Matroska"},
              {"@type":"Video","Format":"AVC"},
              {"@type":"Audio","Format":"AAC","Language":"jpn","Title":"Main","Default":"Yes","Forced":"No"},
              {"@type":"Text","Format":"ASS","Language":"eng","Default":"No","Forced":"Yes"}
            ]}}
            """;

        var result = MediaInfoTrackInspector.ParseOutput(json,
            ["/downloads/Show.S01E01.ja.forced.srt"]);

        var audio = Assert.Single(result.Audio);
        Assert.True(result.HasVideo);
        Assert.Equal("ja", audio.Language);
        Assert.True(audio.IsDefault);
        Assert.Contains(result.Subtitles, x => x.Language == "en" && x.IsForced && !x.IsExternal);
        Assert.Contains(result.Subtitles, x => x.Language == "ja" && x.IsForced && x.IsExternal);
    }

    [Fact]
    public void SmartOrdinaryPlayback_SelectsEnglishAndForcedEnglishSubtitle()
    {
        var tracks = Tracks(audio: ["it", "en"], subtitles: ["en", "en"]);
        tracks.Audio[0].IsDefault = true;
        tracks.Subtitles[0].Title = "English full";
        tracks.Subtitles[1].Title = "English forced";
        tracks.Subtitles[1].IsForced = true;

        var selected = MediaTrackDefaultSelection.Create(new MediaLanguagePolicyDto
        {
            Preference = ReleaseLanguagePreference.Smart,
            PreferredAudioLanguage = "en",
            PreferredSubtitleLanguage = "en",
            PreferForcedSubtitles = true,
            SetPreferredTracksAsDefault = true
        }, tracks, isAnime: false);

        Assert.NotNull(selected);
        Assert.Equal(2, selected.AudioOrdinal);
        Assert.Equal(2, selected.SubtitleOrdinal);
        Assert.True(selected.EditSubtitles);
    }

    [Fact]
    public void SmartAnimePlayback_SelectsJapaneseAndFullEnglishSubtitle()
    {
        var tracks = Tracks(audio: ["en", "ja"], subtitles: ["en", "en"]);
        tracks.Audio[0].IsDefault = true;
        tracks.Subtitles[0].Title = "Signs & songs";
        tracks.Subtitles[0].IsForced = true;
        tracks.Subtitles[1].Title = "English full subtitles";

        var selected = MediaTrackDefaultSelection.Create(new MediaLanguagePolicyDto
        {
            Preference = ReleaseLanguagePreference.Smart,
            PreferredAudioLanguage = "en",
            PreferredSubtitleLanguage = "en",
            SetPreferredTracksAsDefault = true
        }, tracks, isAnime: true);

        Assert.NotNull(selected);
        Assert.Equal(2, selected.AudioOrdinal);
        Assert.Equal(2, selected.SubtitleOrdinal);
        Assert.True(selected.EditSubtitles);
    }

    [Fact]
    public void PlaybackDefaultsCanPreserveReleaseFlags()
    {
        var selected = MediaTrackDefaultSelection.Create(new MediaLanguagePolicyDto
        {
            Preference = ReleaseLanguagePreference.Smart,
            SetPreferredTracksAsDefault = false
        }, Tracks(audio: ["it", "en"]), isAnime: false);

        Assert.Null(selected);
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

    [Fact]
    public async Task OrganizerRejectsAFileWithNoReadableVideoStream()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source.mkv");
            await File.WriteAllBytesAsync(source, [0, 0, 0, 0]);
            var tracks = Tracks(audio: ["en", "ja"]);
            tracks.HasVideo = false;

            var result = await CreateOrganizer(new FixedInspector(tracks)).OrganizeAsync(MovieJob(Path.Combine(root, "library")),
                new TransferItem("transfer", null, null, false), source, Preferences(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(BlocklistReason.MediaPolicyMismatch, result.BlocklistReason);
            Assert.Contains("no readable video", result.FailReason, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OrganizerAppliesDefaultsOnlyToStagedLibraryCopyAndAuditsNewFlags()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source.mkv");
            await File.WriteAllTextAsync(source, "torrent-payload");
            var observed = Tracks(audio: ["it", "en", "ja"]);
            observed.Audio[0].IsDefault = true;
            var inspector = new ApplyingInspector(observed);

            var result = await CreateOrganizer(inspector).OrganizeAsync(MovieJob(Path.Combine(root, "library")),
                new TransferItem("transfer", null, null, false), source, Preferences(), CancellationToken.None);

            Assert.True(result.Success, result.FailReason);
            Assert.Equal("torrent-payload", await File.ReadAllTextAsync(source));
            var video = Assert.Single(result.Files, x => x.FileType == "video");
            Assert.Equal("torrent-payload-defaults-updated", await File.ReadAllTextAsync(video.DestinationPath));
            Assert.False(video.MediaTracks!.Audio[0].IsDefault);
            Assert.True(video.MediaTracks.Audio[1].IsDefault);
            Assert.Equal(2, inspector.Selection?.AudioOrdinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Required_sidecar_is_verified_and_imported_even_when_general_retention_is_off()
    {
        var root = NewRoot();
        try
        {
            var source = Path.Combine(root, "source.mkv");
            var subtitle = Path.Combine(root, "source.en.srt");
            await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
            await File.WriteAllTextAsync(subtitle, "subtitle");
            var library = Path.Combine(root, "library");
            var inspector = new SidecarInspector();
            var job = MovieJob(library);
            job.MediaLanguagePolicy!.RequiredSubtitleLanguages = ["en"];
            var preferences = Preferences(keepSubtitles: false);

            var result = await CreateOrganizer(inspector).OrganizeAsync(job,
                new TransferItem("transfer", null, null, false), root, preferences, CancellationToken.None);

            Assert.True(result.Success, result.FailReason);
            Assert.Equal([subtitle], inspector.Companions);
            var importedSubtitle = Assert.Single(result.Files, x => x.FileType == "subtitle");
            Assert.True(File.Exists(importedSubtitle.DestinationPath));
            Assert.EndsWith(".en.srt", importedSubtitle.DestinationPath, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static LibraryOrganizer CreateOrganizer(IMediaTrackInspector inspector) => new(
        new NoArchives(), new NoSeasonPacks(), new NoEpisodes(), new PlexNamingService(),
        new ReleaseParser(), inspector, NullLogger<LibraryOrganizer>.Instance);

    private static EffectiveLibraryOrganization Preferences(bool keepSubtitles = true) => new()
    {
        TransferMode = TransferMode.Copy,
        MinVideoFileSizeMb = 0,
        KeepSubtitles = keepSubtitles
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
        HasVideo = true,
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

    private sealed class ApplyingInspector(MediaTrackSummaryDto tracks) : IMediaTrackInspector
    {
        public MediaTrackDefaultSelection? Selection { get; private set; }

        public Task<MediaTrackSummaryDto> InspectAsync(string mediaPath,
            IReadOnlyList<string> companionSubtitles, CancellationToken ct) => Task.FromResult(tracks);

        public bool SetDefaults(string stagedPath, string sourceExtension, MediaTrackDefaultSelection selection)
        {
            Selection = selection;
            File.AppendAllText(stagedPath, "-defaults-updated");
            return true;
        }
    }

    private sealed class SidecarInspector : IMediaTrackInspector
    {
        public IReadOnlyList<string> Companions { get; private set; } = [];

        public Task<MediaTrackSummaryDto> InspectAsync(string mediaPath,
            IReadOnlyList<string> companionSubtitles, CancellationToken ct)
        {
            Companions = companionSubtitles;
            const string json = """
                {"media":{"track":[
                  {"@type":"Video","Format":"AVC"},
                  {"@type":"Audio","Format":"AAC","Language":"eng"},
                  {"@type":"Audio","Format":"AAC","Language":"jpn"}
                ]}}
                """;
            return Task.FromResult(MediaInfoTrackInspector.ParseOutput(json, companionSubtitles));
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
