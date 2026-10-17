using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>Matches each outstanding TV episode to at most one allowlisted official upload and freezes the result for the
/// downloader. Like the music resolver, this runs on the web side, so the VPN container never calls YouTube.</summary>
public sealed class DirectVideoEpisodeResolver(IDirectVideoSettingsService settings, IYouTubeVideoCatalog catalog,
    IMediaMetadataProvider metadata, ILogger<DirectVideoEpisodeResolver> logger) : IDirectVideoEpisodeResolver
{
    private const int MaximumTitleLength = 300;

    public async Task<DirectVideoAcquisitionContextDto?> ResolveAsync(FulfillmentJobDto job,
        CancellationToken cancellationToken = default)
    {
        var config = await settings.GetAsync(cancellationToken);
        if (!config.Enabled || !config.ApiKeyConfigured || config.ChannelIds.Count == 0) return null;
        if (job.MediaType != MediaType.TvShow || job.IsAnime || job.TmdbId is not > 0) return null;

        var resolved = new DirectVideoAcquisitionContextDto { ResolvedAt = DateTime.UtcNow };
        var targets = OutstandingTargets(job);
        if (targets.Count == 0) return resolved;

        var uploads = new List<YouTubeCatalogVideo>();
        var readable = 0;
        foreach (var channelId in config.ChannelIds)
        {
            var channelUploads = await catalog.GetChannelUploadsAsync(channelId, cancellationToken);
            if (channelUploads is null) continue;
            readable++;
            uploads.AddRange(channelUploads);
        }
        // Nothing readable means the source was unavailable, not that no episode exists. Leave the job unresolved so it
        // is retried on a later claim, and torrents keep running in the meantime.
        if (readable == 0) return null;

        var names = await SeasonEpisodeNamesAsync(job.TmdbId!.Value, targets, cancellationToken);
        var matches = new List<(int Season, int Episode, YouTubeCatalogVideo Video)>();
        foreach (var (season, episode) in targets)
        {
            names.TryGetValue((season, episode), out var episodeTitle);
            var outcome = YouTubeEpisodeMatcher.Match(job.Title, season, episode, episodeTitle,
                config.ChannelIds, uploads);
            if (outcome.Selected is not { } video)
            {
                logger.LogInformation("No confident YouTube match for {Show} S{Season:D2}E{Episode:D2} ({Count} upload(s) considered)",
                    job.Title, season, episode, outcome.Candidates.Count);
                continue;
            }
            matches.Add((season, episode, video));
        }

        // One upload can name two episodes by title alone (a paired-segment upload). Freezing it for both would make the
        // downloader fetch one payload for two episodes, so an upload used by more than one target is dropped for all.
        var shared = matches.GroupBy(m => m.Video.VideoId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var match in matches)
        {
            if (shared.Contains(match.Video.VideoId))
            {
                logger.LogInformation("YouTube upload {VideoId} matches several episodes of {Show}; skipping S{Season:D2}E{Episode:D2}",
                    match.Video.VideoId, job.Title, match.Season, match.Episode);
                continue;
            }
            resolved.Episodes.Add(new DirectVideoEpisodeDto
            {
                Season = match.Season,
                Episode = match.Episode,
                VideoId = match.Video.VideoId,
                Title = Truncate(match.Video.Title, MaximumTitleLength),
                ChannelId = match.Video.ChannelId,
                DurationSeconds = match.Video.DurationSeconds
            });
        }
        return resolved;
    }

    /// <summary>An explicit episode request is authoritative. Whole-season requests use the missing episodes frozen at
    /// enqueue, the same rule the downloader's candidate source applies.</summary>
    private static List<(int Season, int Episode)> OutstandingTargets(FulfillmentJobDto job)
    {
        var targets = job.RequestedEpisodes.Count > 0
            ? job.RequestedEpisodes.Select(x => (Season: x.Season, Episode: x.Episode))
            : job.SeasonTargets.SelectMany(t => t.MissingEpisodes.Select(e => (Season: t.Season, Episode: e)));
        return targets.Where(x => x.Season > 0 && x.Episode > 0)
            .Distinct()
            .OrderBy(x => x.Season).ThenBy(x => x.Episode)
            .ToList();
    }

    private async Task<Dictionary<(int Season, int Episode), string>> SeasonEpisodeNamesAsync(int tmdbId,
        IReadOnlyList<(int Season, int Episode)> targets, CancellationToken ct)
    {
        var names = new Dictionary<(int Season, int Episode), string>();
        foreach (var season in targets.Select(t => t.Season).Distinct())
        {
            try
            {
                var episodes = await metadata.GetSeasonEpisodesAsync(tmdbId, season);
                foreach (var episode in episodes.Where(x => !string.IsNullOrWhiteSpace(x.Name)).DistinctBy(x => x.EpisodeNumber))
                    names[(season, episode.EpisodeNumber)] = episode.Name!;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Without a title the matcher still accepts an explicit S/E token; it only loses the title path.
                logger.LogWarning(ex, "Episode titles for TMDb {TmdbId} season {Season} unavailable", tmdbId, season);
            }
        }
        return names;
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();
}
