using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared.Releases;

/// <summary>Admits a frozen YouTube episode by identity rather than torrent health. The web app already proved each
/// video against one TMDb episode before the job was claimed, so this policy only confirms that the frozen identity
/// is an episode the job still wants. A direct copy ranks below every torrent: it is a fallback, never a replacement.</summary>
public static class DirectVideoReleaseEvaluator
{
    /// <summary>Far below any torrent score, so the planner selects a direct copy only when nothing else is acceptable.</summary>
    public const double FallbackScore = -10_000;

    /// <summary>The default yt-dlp format chain (22/18) never delivers more than 720p, because the image has no ffmpeg
    /// to merge separate streams. A job that requires more is refused here rather than downloaded and rejected later.</summary>
    public const int MaximumFallbackHeight = 720;

    public static RankedCandidate Evaluate(ReleaseCandidate candidate, FulfillmentJobDto job, RankingContext context,
        ParsedRelease parsed)
    {
        var rejections = new List<Rejection>();
        if (job.MediaType != MediaType.TvShow)
            rejections.Add(new(RejectionReason.MediaTypeMismatch, "YouTube episodes are only accepted for TV shows"));
        if (job.Quality != Quality.Any && (int)job.Quality > MaximumFallbackHeight)
            rejections.Add(new(RejectionReason.BelowQualityFloor,
                $"a YouTube fallback delivers at most {MaximumFallbackHeight}p; this job requires {(int)job.Quality}p"));
        var sourceId = candidate.Acquisition.SourceId;
        if (sourceId is not null && (context.BlocklistedHashes.Contains(sourceId)
            || context.BlocklistedHashes.Contains(AcquisitionResource.BlocklistKey(AcquisitionProtocol.DirectVideo, sourceId))))
            rejections.Add(new(RejectionReason.Blocklisted, "this YouTube video already failed for this request"));

        var identified = DirectVideoLocator.TryDecode(candidate.Acquisition.Locator, out var locator);
        if (!identified)
            rejections.Add(new(RejectionReason.MetadataIncomplete, "the YouTube episode identity is malformed"));
        else if (candidate.Season != locator.Season || candidate.Episode != locator.Episode)
            rejections.Add(new(RejectionReason.WrongEpisode,
                $"the frozen video is for S{locator.Season:D2}E{locator.Episode:D2}, not the candidate's episode"));
        else if (!IsOutstandingTarget(job, locator.Season, locator.Episode))
            rejections.Add(new(RejectionReason.WrongEpisode,
                $"S{locator.Season:D2}E{locator.Episode:D2} is not an outstanding target of this job"));

        var season = identified ? locator.Season : (int?)null;
        var episode = identified ? locator.Episode : (int?)null;
        return new RankedCandidate
        {
            Candidate = candidate,
            Parsed = parsed,
            Resolution = 0,
            Season = season,
            Episode = episode,
            CanonicalEpisodeCoverage = identified
                ? [new EpisodeRef { Season = locator.Season, Episode = locator.Episode }]
                : Array.Empty<EpisodeRef>(),
            IsPack = false,
            Accepted = rejections.Count == 0,
            Rejections = rejections,
            Score = FallbackScore,
            ScoreBreakdown = [new ScoreComponent("direct YouTube fallback", FallbackScore)]
        };
    }

    /// <summary>An explicit episode request is authoritative. Whole-season requests fall back to the missing episodes
    /// frozen at enqueue, mirroring how the planner chooses targets.</summary>
    public static bool IsOutstandingTarget(FulfillmentJobDto job, int season, int episode) =>
        job.RequestedEpisodes.Count > 0
            ? job.RequestedEpisodes.Any(x => x.Season == season && x.Episode == episode)
            : job.SeasonTargets.Any(x => x.Season == season && x.MissingEpisodes.Contains(episode));
}
