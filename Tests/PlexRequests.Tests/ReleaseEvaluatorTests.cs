using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using Xunit;

namespace PlexRequests.Tests;

/// <summary>One test per rejection reason, plus the profile behaviour that Phase 3 made possible.</summary>
public class ReleaseEvaluatorTests
{
    private readonly ReleaseEvaluator _eval = TestData.Evaluator();

    private static bool Rejected(RankedCandidate r, RejectionReason reason) =>
        r.Rejections.Any(x => x.Reason == reason);

    [Fact]
    public void Accepts_a_release_matching_the_profile()
    {
        var defs = TestData.Definitions();
        var r = _eval.Evaluate(
            TestData.Release("Severance.S02E07.1080p.WEB-DL-NTb"),
            TestData.Job(),
            TestData.Context(TestData.Profile(defs), defs: defs));

        Assert.True(r.Accepted);
        Assert.Equal(1080, r.Resolution);
        Assert.Equal(TestData.TierId(defs, Quality.FullHD, ReleaseSource.WebDl), r.QualityDefinitionId);
    }

    [Theory]
    [InlineData("Severance.S02E07.1080p.WEB-DL.English", "en", false)]
    [InlineData("Severance.S02E07.1080p.WEB-DL.Japanese", "ja", false)]
    [InlineData("Severance.S02E07.1080p.WEB-DL.Japanese", "en", true)]
    [InlineData("Severance.S02E07.1080p.WEB-DL.Dual.Audio", "en,ja", false)]
    [InlineData("Severance.S02E07.1080p.WEB-DL.MULTi", "en,ja", false)]
    public void Release_language_hints_are_normalized_without_treating_ambiguous_tags_as_proof(
        string releaseName, string allowed, bool rejected)
    {
        var defs = TestData.Definitions();
        var profile = TestData.Profile(defs);
        profile.AllowedLanguagesCsv = allowed;

        var ranked = _eval.Evaluate(TestData.Release(releaseName), TestData.Job(),
            TestData.Context(profile, defs: defs));

        Assert.Equal(rejected, Rejected(ranked, RejectionReason.LanguageNotAllowed));
    }

    [Fact]
    public void Smart_default_PrefersNormalUnlabelledReleaseOverItalianFirstMultilingualRelease()
    {
        var defs = TestData.Definitions();
        var profile = TestData.Profile(defs);
        profile.LanguagePreference = ReleaseLanguagePreference.Smart;
        profile.PreferredAudioLanguage = "en";

        var english = _eval.Evaluate(TestData.Release("Elle.S01E02.1080p.WEB-DL-FLUX"),
            TestData.Job(title: "Elle"), TestData.Context(profile, defs: defs));
        var italianFirst = _eval.Evaluate(TestData.Release("Elle.S01E02.1080p.WEB-DL.ITA.ENG"),
            TestData.Job(title: "Elle"), TestData.Context(profile, defs: defs));

        Assert.True(english.Score > italianFirst.Score);
    }

    [Fact]
    public void Smart_anime_PrefersDualThenSubbedWithoutRejectingFallbacks()
    {
        var defs = TestData.Definitions();
        var profile = new QualityProfileDto
        {
            Id = 1,
            Name = "Smart anime",
            Items = TestData.Profile(defs).Items,
            CutoffQualityDefinitionId = TestData.TierId(defs, Quality.FullHD, ReleaseSource.WebDl),
            LanguagePreference = ReleaseLanguagePreference.Smart,
            PreferredAudioLanguage = "en",
            PreferForcedSubtitles = true
        };
        var job = TestData.Job(title: "Anime");
        job.IsAnime = true;
        var context = TestData.Context(profile, defs: defs);

        var dual = _eval.Evaluate(TestData.Release("Anime.S01E01.1080p.WEB-DL.DUAL.ENG.JPN"), job, context).Score;
        var subbed = _eval.Evaluate(TestData.Release("Anime.S01E01.1080p.WEB-DL.JPN.SUB"), job, context).Score;
        var dub = _eval.Evaluate(TestData.Release("Anime.S01E01.1080p.WEB-DL.ENG.DUBBED"), job, context).Score;
        var original = _eval.Evaluate(TestData.Release("Anime.S01E01.1080p.WEB-DL.JPN"), job, context).Score;

        Assert.True(dual > subbed);
        Assert.True(subbed > dub);
        Assert.True(dub > original);
    }

    // The headline behaviour of this phase: quality is judged against the profile's allowed tier list,
    // not just a pixel-height floor.
    [Fact]
    public void Rejects_a_tier_the_profile_disallows()
    {
        var defs = TestData.Definitions();
        var profile = TestData.Profile(defs, floor: Quality.FullHD);
        var r = _eval.Evaluate(
            TestData.Release("Severance.S02E07.720p.WEB-DL-NTb"),
            TestData.Job(),
            TestData.Context(profile, defs: defs));

        Assert.False(r.Accepted);
        Assert.True(Rejected(r, RejectionReason.NotInProfile));
        Assert.Contains("isn't allowed", r.Rejections.First().Detail);
    }

    [Fact]
    public void Relaxing_admits_a_disallowed_tier_but_never_a_CAM()
    {
        var defs = TestData.Definitions();
        var profile = TestData.Profile(defs, floor: Quality.FullHD);

        var settled = _eval.Evaluate(TestData.Release("Severance.S02E07.720p.WEB-DL"),
            TestData.Job(), TestData.Context(profile, relax: true, defs: defs));
        Assert.True(settled.Accepted);

        var cam = _eval.Evaluate(TestData.Release("Severance.S02E07.720p.CAM"),
            TestData.Job(), TestData.Context(profile, relax: true, defs: defs));
        Assert.False(cam.Accepted);
        Assert.True(Rejected(cam, RejectionReason.NotInProfile));
    }

    // The cutoff is a ceiling, not just where auto-upgrade searches stop. TestData.Profile marks every
    // tier from the floor up through 4K "allowed" — exactly what every seeded production profile does —
    // so without this check a 1080p target would happily accept a 2160p release because nothing said no.
    [Fact]
    public void Rejects_a_tier_above_the_profiles_cutoff()
    {
        var defs = TestData.Definitions();
        var profile = TestData.Profile(defs, floor: Quality.HD, cutoff: Quality.FullHD);
        var r = _eval.Evaluate(
            TestData.Release("Severance.S02E07.2160p.WEB-DL-NTb"),
            TestData.Job(),
            TestData.Context(profile, defs: defs));

        Assert.False(r.Accepted);
        Assert.True(Rejected(r, RejectionReason.AboveCutoff));
    }

    [Fact]
    public void Relaxing_admits_a_tier_above_cutoff_when_nothing_at_target_was_found()
    {
        var defs = TestData.Definitions();
        var profile = TestData.Profile(defs, floor: Quality.HD, cutoff: Quality.FullHD);
        var r = _eval.Evaluate(
            TestData.Release("Severance.S02E07.2160p.WEB-DL-NTb"),
            TestData.Job(),
            TestData.Context(profile, relax: true, defs: defs));

        Assert.True(r.Accepted);
    }

    [Fact]
    public void Falls_back_to_the_flat_floor_when_a_job_has_no_profile()
    {
        var r = _eval.Evaluate(TestData.Release("Severance.S02E07.720p.WEB-DL"),
            TestData.Job(quality: Quality.FullHD), TestData.Context(profile: null));
        Assert.False(r.Accepted);
        Assert.True(Rejected(r, RejectionReason.BelowQualityFloor));
    }

    [Fact]
    public void Resolves_an_unknown_source_to_the_Unknown_tier()
    {
        // A large share of real release names carry no source token. Without the Unknown-source tier these
        // would resolve to nothing and be unrankable.
        var defs = TestData.Definitions();
        var r = _eval.Evaluate(TestData.Release("Severance.S02E07.1080p"),
            TestData.Job(), TestData.Context(TestData.Profile(defs), defs: defs));

        Assert.True(r.Accepted);
        Assert.Equal(TestData.TierId(defs, Quality.FullHD, ReleaseSource.Unknown), r.QualityDefinitionId);
    }

    [Fact]
    public void Rejects_too_few_seeders()
    {
        var r = _eval.Evaluate(TestData.Release("Severance.S02E07.1080p.WEB-DL", seeders: 0),
            TestData.Job(), TestData.Context());
        Assert.True(Rejected(r, RejectionReason.TooFewSeeders));
    }

    // Two scrapers routinely fail to parse seeders and size. Treating that as a genuine zero silently
    // discarded much of their output against the minimum-seeder and minimum-size gates.
    [Fact]
    public void Unknown_seeders_and_size_are_not_rejections()
    {
        var r = _eval.Evaluate(
            TestData.Release("Severance.S02E07.1080p.WEB-DL", seeders: 0, sizeGb: 0, seedersKnown: false, sizeKnown: false),
            TestData.Job(), TestData.Context());

        Assert.True(r.Accepted);
        Assert.DoesNotContain(r.Rejections, x => x.Reason == RejectionReason.TooFewSeeders);
        Assert.DoesNotContain(r.Rejections, x => x.Reason == RejectionReason.SizeTooSmall);
        // ...but it should score worse than an equivalent release with known seeders.
        var known = _eval.Evaluate(TestData.Release("Severance.S02E07.1080p.WEB-DL", seeders: 50),
            TestData.Job(), TestData.Context());
        Assert.True(known.Score > r.Score);
    }

    [Theory]
    [InlineData(0.01, RejectionReason.SizeTooSmall)]
    [InlineData(999, RejectionReason.SizeTooLarge)]
    public void Rejects_implausible_sizes(double sizeGb, RejectionReason expected)
    {
        var r = _eval.Evaluate(TestData.Release("Severance.S02E07.1080p.WEB-DL", sizeGb: sizeGb),
            TestData.Job(), TestData.Context());
        Assert.True(Rejected(r, expected));
    }

    [Fact]
    public void Rejects_a_different_title()
    {
        var r = _eval.Evaluate(TestData.Release("Completely.Different.Show.S01E01.1080p.WEB-DL"),
            TestData.Job(title: "Severance"), TestData.Context());
        Assert.True(Rejected(r, RejectionReason.TitleMismatch));
    }

    // The short-title false positive: a one-word request must not match a longer title that contains it.
    [Fact]
    public void Rejects_a_longer_title_containing_the_request()
    {
        var r = _eval.Evaluate(TestData.Release("Lucky.Star.S01.1080p.WEB-DL"),
            TestData.Job(title: "Lucky", type: MediaType.TvShow), TestData.Context());
        Assert.True(Rejected(r, RejectionReason.ExtraTitleTokens));
    }

    [Fact]
    public void Accepts_a_regional_variant()
    {
        // "US"/"UK"/"AU" disambiguate a country's edition; they aren't evidence of a different show.
        var r = _eval.Evaluate(TestData.Release("The.Office.US.S01E01.1080p.WEB-DL"),
            TestData.Job(title: "The Office"), TestData.Context());
        Assert.DoesNotContain(r.Rejections, x => x.Reason == RejectionReason.ExtraTitleTokens);
    }

    [Fact]
    public void An_imdb_id_match_overrides_fuzzy_title_matching()
    {
        // The id is authoritative, so an oddly-named release with the right id is still accepted.
        var r = _eval.Evaluate(
            TestData.Release("Totally.Unrecognisable.Name.1080p.WEB-DL", imdbId: "tt11280740"),
            TestData.Job(imdbId: "tt11280740"), TestData.Context());
        Assert.True(r.Accepted);
        Assert.Contains(r.ScoreBreakdown, c => c.Name == "IMDb id match");
    }

    [Fact]
    public void An_imdb_id_mismatch_is_a_hard_rejection()
    {
        var r = _eval.Evaluate(TestData.Release("Severance.S02E07.1080p.WEB-DL", imdbId: "tt0000001"),
            TestData.Job(imdbId: "tt11280740"), TestData.Context());
        Assert.True(Rejected(r, RejectionReason.ImdbMismatch));
    }

    [Fact]
    public void Rejects_a_mismatched_year()
    {
        var r = _eval.Evaluate(TestData.Release("Dune.1984.1080p.BluRay"),
            TestData.Job(title: "Dune", type: MediaType.Movie, year: 2021), TestData.Context());
        Assert.True(Rejected(r, RejectionReason.YearMismatch));
    }

    [Fact]
    public void Rejects_a_tv_release_for_a_movie_request()
    {
        var r = _eval.Evaluate(TestData.Release("Dune.S01E01.1080p.WEB-DL"),
            TestData.Job(title: "Dune", type: MediaType.Movie), TestData.Context());
        Assert.True(Rejected(r, RejectionReason.MediaTypeMismatch));
    }

    [Fact]
    public void Rejects_a_blocklisted_release_by_hash()
    {
        var hash = new string('a', 40);
        var context = TestData.Context(blocklist: new HashSet<string> { hash });
        var r = _eval.Evaluate(TestData.Release("Severance.S02E07.1080p.WEB-DL", infoHash: hash),
            TestData.Job(), context);
        Assert.True(Rejected(r, RejectionReason.Blocklisted));
    }

    [Fact]
    public void Blocklisting_works_even_when_the_indexer_reported_no_hash()
    {
        // Only some indexers report an info hash, so it's derived from the magnet. Without that, a failed
        // release from any other indexer could be grabbed again immediately.
        var hash = new string('b', 40);
        var original = TestData.Release("Severance.S02E07.1080p.WEB-DL", infoHash: hash);
        var candidate = original with { Acquisition = original.Acquisition with { SourceId = null } };
        var r = _eval.Evaluate(candidate, TestData.Job(),
            TestData.Context(blocklist: new HashSet<string> { hash }));
        Assert.True(Rejected(r, RejectionReason.Blocklisted));
    }

    [Fact]
    public void An_upgrade_never_accepts_a_downgrade_even_when_relaxed()
    {
        var r = _eval.Evaluate(TestData.Release("Severance.S02E07.720p.WEB-DL"),
            TestData.Job(quality: Quality.FullHD, isUpgrade: true),
            TestData.Context(relax: true));
        Assert.True(Rejected(r, RejectionReason.NotAnUpgrade));
    }

    [Fact]
    public void Higher_profile_rank_outscores_more_seeders()
    {
        // Profile position must dominate: the tier the admin ranked higher should win even when a worse
        // tier has far better seeding.
        var defs = TestData.Definitions();
        var context = TestData.Context(TestData.Profile(defs, floor: Quality.HD), defs: defs);

        var better = _eval.Evaluate(TestData.Release("Severance.S02E07.1080p.WEB-DL", seeders: 5), TestData.Job(), context);
        var worse = _eval.Evaluate(TestData.Release("Severance.S02E07.720p.WEB-DL", seeders: 5000), TestData.Job(), context);

        Assert.True(better.Accepted && worse.Accepted);
        Assert.True(better.Score > worse.Score, $"1080p scored {better.Score}, 720p scored {worse.Score}");
    }

    [Fact]
    public void Score_breakdown_explains_the_result()
    {
        var r = _eval.Evaluate(TestData.Release("Severance.S02E07.1080p.WEB-DL.x265-NTb", seeders: 500),
            TestData.Job(), TestData.Context());
        Assert.NotEmpty(r.ScoreBreakdown);
        Assert.Equal(r.Score, r.ScoreBreakdown.Sum(c => c.Points), 3);
    }

    [Fact]
    public void MeetsCutoff_compares_tier_weight_not_resolution_alone()
    {
        var defs = TestData.Definitions();
        var profile = TestData.Profile(defs, cutoff: Quality.FullHD);
        Assert.True(ReleaseEvaluator.MeetsCutoff(TestData.TierId(defs, Quality.UHD4K, ReleaseSource.WebDl), profile, defs));
        Assert.False(ReleaseEvaluator.MeetsCutoff(TestData.TierId(defs, Quality.HD, ReleaseSource.Remux), profile, defs));
        Assert.False(ReleaseEvaluator.MeetsCutoff(null, profile, defs));
    }

    [Fact]
    public void Music_uses_audio_quality_instead_of_video_profiles()
    {
        var job = MusicJob(RequestScopeKind.Album, "Random Access Memories", "Daft Punk");
        var result = _eval.Evaluate(
            TestData.Release("Daft.Punk-Random.Access.Memories-2013-FLAC-24bit-96kHz", sizeGb: 1.5),
            job,
            TestData.Context(TestData.Profile(TestData.Definitions())));

        Assert.True(result.Accepted);
        Assert.Equal(0, result.Resolution);
        Assert.Contains(result.ScoreBreakdown, x => x.Name == "Audio format" && x.Points == 400);
        Assert.Contains(result.ScoreBreakdown, x => x.Name == "Bit depth");
    }

    [Fact]
    public void Music_rejects_same_named_video_and_wrong_artist()
    {
        var job = MusicJob(RequestScopeKind.Album, "Discovery", "Daft Punk");
        var video = _eval.Evaluate(TestData.Release("Discovery.2021.1080p.WEB-DL.AAC", sizeGb: 2),
            job, TestData.Context());
        var wrongArtist = _eval.Evaluate(TestData.Release("Electric.Light.Orchestra-Discovery-1979-FLAC", sizeGb: 1),
            job, TestData.Context());

        Assert.True(Rejected(video, RejectionReason.MediaTypeMismatch));
        Assert.True(Rejected(wrongArtist, RejectionReason.ArtistMismatch));
    }

    [Fact]
    public void Authoritative_music_category_accepts_a_release_that_omits_the_codec_token()
    {
        var job = MusicJob(RequestScopeKind.Album, "Discovery", "Daft Punk");
        var candidate = TestData.Release("Daft.Punk-Discovery-2001", sizeGb: .8) with
            { CategoryIds = new[] { 3000 } };

        var result = _eval.Evaluate(candidate, job, TestData.Context());

        Assert.True(result.Accepted);
        Assert.Contains(result.ScoreBreakdown, x => x.Name == "Audio format" && x.Points == 100);
    }

    [Fact]
    public void Album_search_waits_for_artist_metadata_instead_of_guessing_by_title()
    {
        var job = MusicJob(RequestScopeKind.Album, "Discovery", string.Empty);

        var result = _eval.Evaluate(TestData.Release("Discovery-2001-FLAC", sizeGb: .8),
            job, TestData.Context());

        Assert.True(Rejected(result, RejectionReason.MetadataIncomplete));
    }

    [Fact]
    public void Artist_request_requires_a_catalog_release()
    {
        var job = MusicJob(RequestScopeKind.ArtistCatalog, "Daft Punk", "Daft Punk", MediaKind.Artist);
        var album = _eval.Evaluate(TestData.Release("Daft.Punk-Discovery-2001-FLAC", sizeGb: 1),
            job, TestData.Context());
        var catalog = _eval.Evaluate(TestData.Release("Daft.Punk-Complete.Discography-FLAC", sizeGb: 12),
            job, TestData.Context());

        Assert.True(Rejected(album, RejectionReason.CatalogScopeMismatch));
        Assert.True(catalog.Accepted);
    }

    [Fact]
    public void Artist_search_waits_for_its_durable_album_inventory()
    {
        var job = MusicJob(RequestScopeKind.ArtistCatalog, "Daft Punk", "Daft Punk", MediaKind.Artist);
        job.Music!.ExpectedAlbums.Clear();

        var result = _eval.Evaluate(TestData.Release("Daft.Punk-Complete.Discography-FLAC", sizeGb: 12),
            job, TestData.Context());

        Assert.True(Rejected(result, RejectionReason.MetadataIncomplete));
    }

    [Fact]
    public void Music_search_rejects_a_complete_contract_for_the_wrong_request_scope()
    {
        var job = MusicJob(RequestScopeKind.Album, "Discovery", "Daft Punk", MediaKind.Artist);

        var result = _eval.Evaluate(TestData.Release("Daft.Punk-Discovery-FLAC", sizeGb: 1),
            job, TestData.Context());

        Assert.True(Rejected(result, RejectionReason.MetadataIncomplete));
    }

    [Fact]
    public void Lossless_music_outranks_lossy_even_with_fewer_seeders()
    {
        var job = MusicJob(RequestScopeKind.Album, "Discovery", "Daft Punk");
        var flac = _eval.Evaluate(TestData.Release("Daft.Punk-Discovery-FLAC", seeders: 5, sizeGb: 1),
            job, TestData.Context());
        var mp3 = _eval.Evaluate(TestData.Release("Daft.Punk-Discovery-MP3-320", seeders: 500, sizeGb: .2),
            job, TestData.Context());

        Assert.True(flac.Accepted && mp3.Accepted);
        Assert.True(flac.Score > mp3.Score, $"FLAC scored {flac.Score}, MP3 scored {mp3.Score}");
    }

    private static FulfillmentJobDto MusicJob(
        RequestScopeKind scope, string title, string artist, MediaKind kind = MediaKind.Album) => new()
        {
            MediaType = MediaType.Music,
            Title = title,
            RequestScope = scope,
            Music = new MusicAcquisitionContextDto
            {
                Kind = kind,
                Artist = artist,
                Album = kind == MediaKind.Album ? title : null,
                Track = kind == MediaKind.Track ? title : null,
                TrackCount = kind is MediaKind.Album or MediaKind.Track ? 1 : 0,
                Tracks = kind is MediaKind.Album or MediaKind.Track
                    ? [new MusicTrackMetadataDto { RecordingId = "track", Title = title, TrackNumber = 1, DiscNumber = 1 }]
                    : [],
                ExpectedAlbums = kind == MediaKind.Artist ? ["Discovery"] : []
            }
        };
}
