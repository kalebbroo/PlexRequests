using System.Text.RegularExpressions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Shared.Releases;

/// <summary>
/// Music-specific identity and audio-quality policy. It deliberately returns the same <see cref="RankedCandidate"/>
/// contract as video, so search, blocklisting, planning and the admin explanation UI remain shared.
/// </summary>
internal static class MusicReleaseEvaluator
{
    private static readonly HashSet<string> MusicCodecs = new(StringComparer.OrdinalIgnoreCase)
        { "FLAC", "ALAC", "APE", "WAV", "PCM", "LPCM", "MP3", "AAC", "Opus" };

    public static RankedCandidate Evaluate(
        ReleaseCandidate candidate,
        FulfillmentJobDto job,
        RankingContext context,
        ParsedRelease parsed)
    {
        var rejections = new List<Rejection>();
        var music = job.Music;
        var authoritativeDirect = candidate.Acquisition.Protocol == AcquisitionProtocol.DirectAudio;
        var expectedKind = job.RequestScope.ToMediaKind(MediaType.Music);

        if (music?.Kind != expectedKind || music.HasCompletionContract != true)
            rejections.Add(new(RejectionReason.MetadataIncomplete,
                "the durable music completion contract is incomplete; the job will retry after its metadata refresh"));

        var minSeeders = context.Profile?.MinSeeders ?? context.Preferences.MinSeeders;
        if (candidate.SeedersKnown && candidate.Seeders < minSeeders)
            rejections.Add(new(RejectionReason.TooFewSeeders,
                $"{candidate.Seeders} seeders, minimum is {minSeeders}"));

        var (minSize, maxSize) = job.RequestScope switch
        {
            RequestScopeKind.Track => (0.003, 1d),
            RequestScopeKind.ArtistCatalog => (0.05, 100d),
            _ => (0.015, 8d)
        };
        if (candidate.SizeKnown)
        {
            if (candidate.SizeGb < minSize)
                rejections.Add(new(RejectionReason.SizeTooSmall,
                    $"{candidate.SizeGb:F3} GB is implausibly small for this music request"));
            else if (candidate.SizeGb > maxSize)
                rejections.Add(new(RejectionReason.SizeTooLarge,
                    $"{candidate.SizeGb:F1} GB exceeds the {maxSize:F0} GB music safety limit"));
        }

        var musicCategory = candidate.CategoryIds.Any(x => x is >= 3000 and < 4000);
        if (!authoritativeDirect && (parsed.AudioCodec is null || !MusicCodecs.Contains(parsed.AudioCodec)) && !musicCategory)
            rejections.Add(new(RejectionReason.MusicFormatMissing,
                "neither a supported audio format nor an authoritative music category was identified"));

        // A same-named video is a dangerous false positive. Audio releases have no video resolution or
        // S/E markers; reject those even if an audio codec token also happens to be present.
        if (!authoritativeDirect && (parsed.Resolution > 0 || parsed.Season is not null || parsed.Episode is not null))
            rejections.Add(new(RejectionReason.MediaTypeMismatch, "this looks like a video release, not music"));

        var artistRecall = authoritativeDirect || string.IsNullOrWhiteSpace(music?.Artist)
            ? 1d : ReleaseEvaluator.TitleSimilarity(parsed.Title, music.Artist);
        if (artistRecall < context.Preferences.MinTitleSimilarity)
            rejections.Add(new(RejectionReason.ArtistMismatch,
                $"the release only matches {artistRecall:P0} of artist \"{music!.Artist}\""));

        var requestedTitle = job.RequestScope switch
        {
            RequestScopeKind.Track => music?.Track ?? job.Title,
            RequestScopeKind.ArtistCatalog => music?.Artist ?? job.Title,
            _ => music?.Album ?? job.Title
        };
        var titleRecall = authoritativeDirect ? 1d : ReleaseEvaluator.TitleSimilarity(parsed.Title, requestedTitle);
        if (titleRecall < context.Preferences.MinTitleSimilarity)
            rejections.Add(new(RejectionReason.TitleMismatch,
                $"the release only matches {titleRecall:P0} of \"{requestedTitle}\""));

        if (job.RequestScope == RequestScopeKind.ArtistCatalog &&
            !Regex.IsMatch(candidate.ReleaseName,
                @"\b(discography|discographies|complete|collection|anthology)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            rejections.Add(new(RejectionReason.CatalogScopeMismatch,
                "an artist-catalog request requires a discography, complete, collection or anthology release"));

        var sourceId = candidate.Acquisition.Protocol == AcquisitionProtocol.Torrent
            ? MagnetUtil.Normalize(candidate.Acquisition.SourceId) ?? MagnetUtil.InfoHashFromMagnet(candidate.Acquisition.Locator)
            : candidate.Acquisition.SourceId;
        if (sourceId is not null && (context.BlocklistedHashes.Contains(sourceId)
            || context.BlocklistedHashes.Contains(AcquisitionResource.BlocklistKey(candidate.Acquisition.Protocol, sourceId))))
            rejections.Add(new(RejectionReason.Blocklisted, "this release already failed for this request"));
        if (string.IsNullOrWhiteSpace(candidate.Acquisition.Locator))
            rejections.Add(new(RejectionReason.MissingAcquisition, "no acquisition locator"));

        var components = new List<ScoreComponent>();
        void Add(string name, double points) { if (points != 0) components.Add(new(name, points)); }

        Add("Artist match", artistRecall * 100);
        Add(job.RequestScope == RequestScopeKind.ArtistCatalog ? "Catalog match" : "Title match", titleRecall * 100);
        if (authoritativeDirect) Add("Authoritative direct source", 1000);
        Add("Audio format", parsed.AudioCodec switch
        {
            // Format preference must dominate seeder popularity: a heavily seeded MP3 is easier to fetch,
            // not a better archival result than a healthy lossless release.
            "FLAC" or "ALAC" or "APE" or "WAV" or "PCM" or "LPCM" => 400,
            "Opus" => 210,
            "AAC" => 180,
            "MP3" => 160,
            _ when musicCategory => 100,
            _ => 0
        });
        if (parsed.AudioBitDepth is int depth) Add("Bit depth", Math.Max(0, depth - 16) * 4);
        if (parsed.AudioSampleRateKhz is double rate) Add("Sample rate", Math.Min(40, Math.Max(0, rate - 44.1) / 4));
        if (candidate.SeedersKnown) Add("Seeders", Math.Log10(Math.Max(1, candidate.Seeders)) * 80);
        else Add("Seeders unknown", -25);
        if (job.Year is int wantedYear && parsed.Year is int releaseYear && Math.Abs(wantedYear - releaseYear) <= 1)
            Add("Release year", 25);
        if (context.IndexerPriorities.TryGetValue(candidate.IndexerId, out var priority))
            Add("Indexer priority", Math.Clamp(50 - priority, 0, 49));

        return new RankedCandidate
        {
            Candidate = candidate,
            Parsed = parsed,
            Resolution = 0,
            Accepted = rejections.Count == 0,
            Rejections = rejections,
            Score = components.Sum(x => x.Points),
            ScoreBreakdown = components
        };
    }
}
