using System.Net.Http.Json;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;
using PlexRequestsHosted.Shared;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// EZTV public JSON API (TV/anime), keyed by IMDb id: GET /api/get-torrents?imdb_id={digits}.
/// Returns magnet links + seeds/peers + size directly.
/// </summary>
public class EztvIndexerProvider(HttpClient http, ILogger<EztvIndexerProvider> logger)
    : IIndexerImplementation, IReleaseFeedSource
{
    private readonly HttpClient _http = http;
    private readonly ILogger<EztvIndexerProvider> _logger = logger;

    public string Key => "EZTV";

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(IndexerConfigDto indexer, FulfillmentJobDto job, CancellationToken ct)
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
                candidates.Add(ToCandidate(t, indexer, job.ImdbId));
            }
            if (torrents.Count < 100) break;
            page++;
        }

        // Episode-level targets take precedence (monitored/auto or explicit episode requests); otherwise
        // fall back to whole-season filtering. No selection ⇒ everything (whole series).
        // EZTV's season/episode fields are source-native. With an alternate-order profile, comparing them
        // to canonical Plex targets here would discard the exact releases the shared evaluator is meant to
        // translate. Keep the provider's full title feed and let ranking/planning apply the snapshot.
        if (!EpisodeOrderMapping.IsActive(job.EpisodeOrderProfile) && job.RequestedEpisodes.Count > 0)
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
        else if (!EpisodeOrderMapping.IsActive(job.EpisodeOrderProfile) && job.RequestedSeasons.Count > 0)
        {
            candidates = candidates.Where(c => c.Season is null || job.RequestedSeasons.Contains(c.Season.Value)).ToList();
        }

        return candidates;
    }

    public async Task<ReleaseFeedPage> FetchAsync(
        IndexerConfigDto indexer,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var items = new List<CatalogItemDto>();
        const int pageSize = 100;
        const int maxPages = 10;
        for (var page = 1; page <= maxPages; page++)
        {
            var response = await _http.GetFromJsonAsync<EztvResponse>(
                $"/api/get-torrents?limit={pageSize}&page={page}",
                IndexerJson.Options,
                cancellationToken);
            var rows = response?.Torrents ?? new();
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Title) || string.IsNullOrWhiteSpace(row.MagnetUrl)) continue;
                var externalId = row.Id > 0 ? row.Id.ToString() : MagnetUtil.Normalize(row.Hash)
                    ?? MagnetUtil.InfoHashFromMagnet(row.MagnetUrl);
                if (string.IsNullOrWhiteSpace(externalId)) continue;
                var published = row.DateReleasedUnix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(row.DateReleasedUnix).UtcDateTime
                    : (DateTime?)null;
                items.Add(new CatalogItemDto
                {
                    ExternalId = externalId,
                    ReleaseName = row.Title,
                    SourceUrl = row.TorrentUrl,
                    InfoHash = row.Hash,
                    MagnetUri = row.MagnetUrl,
                    ImdbId = row.ImdbId,
                    Seeders = row.Seeds,
                    Leechers = row.Peers,
                    SizeBytes = row.SizeBytes > 0 ? row.SizeBytes : null,
                    PublishedAt = published,
                    MediaType = MediaType.TvShow
                });
            }
            if (string.IsNullOrWhiteSpace(cursor) || rows.Count < pageSize
                || items.Any(x => string.Equals(x.ExternalId, cursor, StringComparison.OrdinalIgnoreCase)))
                break;
        }
        return ReleaseFeedCursor.Select(items, cursor, limit);
    }

    private static ReleaseCandidate ToCandidate(EztvTorrent torrent, IndexerConfigDto indexer, string? fallbackImdbId) => new()
    {
        ReleaseName = torrent.Title ?? string.Empty,
        Acquisition = AcquisitionResource.Torrent(torrent.MagnetUrl!, torrent.Hash),
        ImdbId = string.IsNullOrWhiteSpace(torrent.ImdbId) ? fallbackImdbId : torrent.ImdbId,
        Seeders = torrent.Seeds,
        Leechers = torrent.Peers,
        SizeBytes = torrent.SizeBytes,
        IndexerId = indexer.Id,
        Source = indexer.Name,
        Season = torrent.Season > 0 ? torrent.Season : null,
        Episode = torrent.Episode > 0 ? torrent.Episode : null,
        PublishDate = torrent.DateReleasedUnix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(torrent.DateReleasedUnix).UtcDateTime : null
    };

    private sealed class EztvResponse
    {
        public List<EztvTorrent>? Torrents { get; set; }
    }

    private sealed class EztvTorrent
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? ImdbId { get; set; }
        public string? MagnetUrl { get; set; }
        public string? Hash { get; set; }
        public int Seeds { get; set; }
        public int Peers { get; set; }
        public long SizeBytes { get; set; }
        public int Season { get; set; }
        public int Episode { get; set; }
        public long DateReleasedUnix { get; set; }
        public string? TorrentUrl { get; set; }
    }
}
