using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Indexers;

/// <summary>Turns the web app's frozen YouTube episode identities into one direct candidate per outstanding episode.
/// It makes no network call: matching happened on the web side before the job crossed the VPN boundary.</summary>
public sealed class YouTubeVideoDirectSource(IOptions<DirectVideoOptions> options) : IAcquisitionCandidateSource
{
    public string Name => "YouTube video direct";

    public bool AppliesTo(FulfillmentJobDto job) => options.Value.Enabled
        && job.MediaType == MediaType.TvShow
        && job.DirectVideo is { Episodes.Count: > 0 }
        && job.AllowedAcquisitionProtocols.Contains(AcquisitionProtocol.DirectVideo);

    public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(FulfillmentJobDto job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!AppliesTo(job) || job.DirectVideo is null)
            return Task.FromResult<IReadOnlyList<ReleaseCandidate>>(Array.Empty<ReleaseCandidate>());

        IReadOnlyList<ReleaseCandidate> result = job.DirectVideo.Episodes
            .Where(e => e.Season > 0 && e.Episode > 0
                        && DirectVideoLocator.IsVideoId(e.VideoId)
                        && !string.IsNullOrWhiteSpace(e.Title) && e.Title.Length <= 512)
            .Where(e => DirectVideoReleaseEvaluator.IsOutstandingTarget(job, e.Season, e.Episode))
            .OrderBy(e => e.Season).ThenBy(e => e.Episode)
            .Select(e => new ReleaseCandidate
            {
                ReleaseName = $"{job.Title} S{e.Season:D2}E{e.Episode:D2} {e.Title} [YouTube]",
                Acquisition = AcquisitionResource.DirectVideo(
                    new DirectVideoLocator(1, e.VideoId, e.Season, e.Episode, e.Title, e.ChannelId).Encode(),
                    $"youtube:video:{e.VideoId}"),
                Source = Name,
                Season = e.Season,
                Episode = e.Episode,
                SeedersKnown = false,
                SizeKnown = false,
                CategoryIds = [5000]
            })
            .ToList();
        return Task.FromResult(result);
    }
}
