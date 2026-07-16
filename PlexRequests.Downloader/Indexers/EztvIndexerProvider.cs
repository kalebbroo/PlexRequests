using System.Net.Http.Json;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// EZTV public JSON API (TV/anime), keyed by IMDb id: GET /api/get-torrents?imdb_id={digits}.
/// Returns magnet links + seeds/peers + size directly.
/// </summary>
public class EztvIndexerProvider(HttpClient http, ILogger<EztvIndexerProvider> logger) : IIndexerProvider
{
    private readonly HttpClient _http = http;
    private readonly ILogger<EztvIndexerProvider> _logger = logger;

    public string Name => "EZTV";
    public bool Supports(MediaType mediaType) => mediaType is MediaType.TvShow or MediaType.Anime;

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(FulfillmentJobDto job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.ImdbId))
        {
            _logger.LogWarning("EZTV needs an IMDb id; none on job {JobId} (\"{Title}\")", job.Id, job.Title);
            return Array.Empty<ReleaseCandidate>();
        }

        // "tt0944947" -> "944947" (EZTV wants the numeric id, no tt / leading zeros)
        var imdb = job.ImdbId.TrimStart('t', 'T').TrimStart('0');
        var candidates = new List<ReleaseCandidate>();
        var page = 1;
        const int maxPages = 3;

        while (page <= maxPages && !ct.IsCancellationRequested)
        {
            var resp = await _http.GetFromJsonAsync<EztvResponse>(
                $"/api/get-torrents?imdb_id={imdb}&limit=100&page={page}", IndexerJson.Options, ct);
            var torrents = resp?.Torrents;
            if (torrents is null || torrents.Count == 0) break;

            foreach (var t in torrents)
            {
                if (string.IsNullOrWhiteSpace(t.MagnetUrl)) continue;
                candidates.Add(new ReleaseCandidate
                {
                    ReleaseName = t.Title ?? string.Empty,
                    Magnet = t.MagnetUrl!,
                    InfoHash = t.Hash,
                    Seeders = t.Seeds,
                    Leechers = t.Peers,
                    SizeBytes = t.SizeBytes,
                    Source = Name,
                    Season = t.Season > 0 ? t.Season : null,
                    Episode = t.Episode > 0 ? t.Episode : null
                });
            }
            if (torrents.Count < 100) break;
            page++;
        }

        // Episode-level targets take precedence (monitored/auto or explicit episode requests); otherwise
        // fall back to whole-season filtering. No selection ⇒ everything (whole series).
        if (job.RequestedEpisodes.Count > 0)
        {
            var want = job.RequestedEpisodes.Select(e => (e.Season, e.Episode)).ToHashSet();
            var wantSeasons = job.RequestedEpisodes.Select(e => e.Season).ToHashSet();
            // Keep exact episode matches AND the season packs for the requested seasons: EZTV reports a
            // season pack with episode 0 (→ null here), so an episode-only filter would drop it — but the
            // ranker needs it as a fallback when an episode has no standalone release (see PlanEpisodes).
            candidates = candidates
                .Where(c =>
                    (c.Season is not null && c.Episode is not null && want.Contains((c.Season.Value, c.Episode.Value)))
                    || (c.Episode is null && (c.Season is null || wantSeasons.Contains(c.Season.Value))))
                .ToList();
        }
        else if (job.RequestedSeasons.Count > 0)
        {
            candidates = candidates.Where(c => c.Season is null || job.RequestedSeasons.Contains(c.Season.Value)).ToList();
        }

        return candidates;
    }

    private sealed class EztvResponse
    {
        public List<EztvTorrent>? Torrents { get; set; }
    }

    private sealed class EztvTorrent
    {
        public string? Title { get; set; }
        public string? MagnetUrl { get; set; }
        public string? Hash { get; set; }
        public int Seeds { get; set; }
        public int Peers { get; set; }
        public long SizeBytes { get; set; }
        public int Season { get; set; }
        public int Episode { get; set; }
    }
}
