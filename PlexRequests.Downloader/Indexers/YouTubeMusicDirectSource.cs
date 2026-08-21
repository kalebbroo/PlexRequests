using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Download;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Indexers;

/// <summary>Turns a pre-resolved YouTube manifest into one direct-audio candidate. It performs no catalog
/// request: the web app already matched and froze the track ids before the job crossed the VPN boundary.</summary>
public sealed class YouTubeMusicDirectSource(IOptions<DirectAudioOptions> options) : IAcquisitionCandidateSource
{
    public string Name => "YouTube Music direct";

    public bool AppliesTo(FulfillmentJobDto job) => options.Value.Enabled
        && job.MediaType == MediaType.Music
        && (job.Music?.DirectAudio?.Provider.Equals("youtube", StringComparison.OrdinalIgnoreCase) == true
            || job.Media?.Provider.Equals("youtube", StringComparison.OrdinalIgnoreCase) == true)
        && job.AllowedAcquisitionProtocols.Contains(AcquisitionProtocol.DirectAudio)
        && job.Music?.Kind is MediaKind.Track or MediaKind.Album;

    public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(FulfillmentJobDto job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!AppliesTo(job) || job.Music is null || job.Media is null)
            return Task.FromResult<IReadOnlyList<ReleaseCandidate>>(Array.Empty<ReleaseCandidate>());

        var direct = job.Music.DirectAudio;
        var manifest = direct?.Provider.Equals("youtube", StringComparison.OrdinalIgnoreCase) == true
            ? direct.Tracks : job.Music.Tracks;
        var tracks = manifest
            .Where(x => YouTubeMusicLocator.IsVideoId(x.RecordingId))
            .OrderBy(x => Math.Max(1, x.DiscNumber)).ThenBy(x => Math.Max(1, x.TrackNumber))
            .Select((x, index) => new YouTubeMusicTrack(x.RecordingId, x.Title,
                Math.Max(1, x.DiscNumber), Math.Max(1, x.TrackNumber > 0 ? x.TrackNumber : index + 1),
                x.ArtistCredit ?? job.Music.Artist, job.Music.Album,
                job.Music.TrackCount, job.Year, job.Music.Artist))
            .ToList();
        if (job.Music.Kind == MediaKind.Track) tracks = tracks.Take(1).ToList();
        var expected = job.Music.Kind == MediaKind.Track ? 1 : Math.Max(job.Music.TrackCount, job.Music.Tracks.Count);
        if (tracks.Count == 0 || (expected > 0 && tracks.Count != expected))
            return Task.FromResult<IReadOnlyList<ReleaseCandidate>>(Array.Empty<ReleaseCandidate>());
        var discCount = tracks.Max(x => x.DiscNumber);
        tracks = tracks.Select(x => x with { TrackCount = tracks.Count, DiscCount = discCount }).ToList();

        var directId = direct?.Provider.Equals("youtube", StringComparison.OrdinalIgnoreCase) == true
            ? direct.ExternalId : job.Media.Id;
        var sourceId = $"youtube:{job.Music.Kind.ToString().ToLowerInvariant()}:{directId}";
        var display = string.Join(" - ", new[] { job.Music.Artist, job.Music.Album ?? job.Music.Track ?? job.Title }
            .Where(x => !string.IsNullOrWhiteSpace(x))) + " [YouTube Direct AAC]";
        IReadOnlyList<ReleaseCandidate> result =
        [
            new ReleaseCandidate
            {
                ReleaseName = display,
                Acquisition = AcquisitionResource.DirectAudio(
                    new YouTubeMusicLocator(2, tracks, job.Music.ArtworkUrl).Encode(), sourceId),
                Source = Name,
                SeedersKnown = false,
                SizeKnown = false,
                CategoryIds = [3000],
                QualityLabel = "AAC"
            }
        ];
        return Task.FromResult(result);
    }
}
