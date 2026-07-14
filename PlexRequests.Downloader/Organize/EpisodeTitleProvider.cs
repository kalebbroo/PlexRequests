using System.Collections.Concurrent;
using PlexRequests.Downloader.Api;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Organize;

/// <summary>
/// Lazily fetches TMDB episode titles for a season at import time (not bundled into the job DTO at
/// enqueue time) so a season-pack import can name every episode in the pack — including ones that were
/// already on Plex, and thus excluded from the job's original "missing episodes" list — with the
/// freshest available title data. Cached per (tmdbId, season) for the lifetime of the worker process.
/// </summary>
public interface IEpisodeTitleProvider
{
    Task<IReadOnlyList<EpisodeDto>> GetSeasonEpisodesAsync(int? tmdbId, int season, CancellationToken ct);
    Task<string?> GetEpisodeTitleAsync(int? tmdbId, int season, int episode, CancellationToken ct);
}

public class EpisodeTitleProvider(IPlexRequestsApiClient api, ILogger<EpisodeTitleProvider> logger) : IEpisodeTitleProvider
{
    private readonly ConcurrentDictionary<(int TmdbId, int Season), Task<IReadOnlyList<EpisodeDto>>> _cache = new();

    public Task<IReadOnlyList<EpisodeDto>> GetSeasonEpisodesAsync(int? tmdbId, int season, CancellationToken ct)
    {
        if (tmdbId is not int id) return Task.FromResult<IReadOnlyList<EpisodeDto>>(Array.Empty<EpisodeDto>());
        return _cache.GetOrAdd((id, season), key => FetchAsync(key.TmdbId, key.Season, ct));
    }

    public async Task<string?> GetEpisodeTitleAsync(int? tmdbId, int season, int episode, CancellationToken ct)
    {
        var episodes = await GetSeasonEpisodesAsync(tmdbId, season, ct);
        return episodes.FirstOrDefault(e => e.EpisodeNumber == episode)?.Name;
    }

    private async Task<IReadOnlyList<EpisodeDto>> FetchAsync(int tmdbId, int season, CancellationToken ct)
    {
        try { return await api.GetSeasonEpisodesAsync(tmdbId, season, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Episode title fetch failed for tmdbId={TmdbId} season={Season}; falling back to no titles", tmdbId, season);
            return Array.Empty<EpisodeDto>();
        }
    }
}
