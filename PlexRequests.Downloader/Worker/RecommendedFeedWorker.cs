using PlexRequests.Downloader.Api;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Worker;

/// <summary>
/// Feeds the home page's "Recommended" row with what's currently being uploaded.
///
/// This runs in the downloader, not the web app, for the same reason searching does: the web container sits
/// outside the VPN tunnel, so browsing a torrent site from there would egress tracker traffic on the user's
/// real IP. The downloader browses from behind the tunnel and pushes titles out; the web app only ever sees
/// a list of names it then resolves against TMDB.
/// </summary>
public class RecommendedFeedWorker(
    IEnumerable<IIndexerImplementation> implementations,
    IIndexerSettingsProvider indexerSettings,
    IReleaseParser parser,
    IPlexRequestsApiClient api,
    ILogger<RecommendedFeedWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private const int PerCategory = 60;   // rows to read per media type before filtering
    private const int MaxTitles = 40;     // titles pushed per refresh

    private readonly Dictionary<string, IIndexerImplementation> _byKey =
        implementations.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); } // let startup settle
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Recommended feed refresh failed"); }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        await indexerSettings.RefreshAsync(ct);

        var sources = indexerSettings.All
            .Where(i => i.Enabled && i.EnableRecommendedFeed && _byKey.ContainsKey(i.Implementation))
            .ToList();
        if (sources.Count == 0) return;

        var items = new List<RecommendedFeedItemDto>();
        foreach (var source in sources)
        {
            foreach (var mediaType in new[] { MediaType.Movie, MediaType.TvShow })
            {
                if (ct.IsCancellationRequested) break;
                if (!source.Supports(mediaType)) continue;

                var rows = await _byKey[source.Implementation].BrowseLatestAsync(source, mediaType, PerCategory, ct);
                items.AddRange(ExtractTitles(rows, source, mediaType));
            }
        }

        if (items.Count == 0)
        {
            logger.LogInformation("Recommended feed: nothing usable returned (source blocked or empty)");
            return;
        }

        // Dedupe by title, keeping the newest sighting. A popular title is re-uploaded constantly and would
        // otherwise fill the whole row with itself.
        var deduped = items
            .GroupBy(i => (i.Title.ToLowerInvariant(), i.MediaType))
            .Select(g => g.OrderBy(x => x.Rank).First())
            .OrderBy(i => i.Rank)
            .Take(MaxTitles)
            .ToList();

        var accepted = await api.PushRecommendedAsync(deduped, ct);
        logger.LogInformation("Recommended feed: pushed {Count} title(s), {Accepted} resolved to real metadata",
            deduped.Count, accepted);
    }

    /// <summary>
    /// Turn raw listing rows into requestable titles.
    ///
    /// The filtering is the difference between a useful row and a junk one: a torrent site's newest-uploads
    /// page is dominated by cams, unlabelled rips and foreign-language releases. Applying the same
    /// resolution and source gates the ranker uses keeps this to things somebody would actually want.
    /// </summary>
    private List<RecommendedFeedItemDto> ExtractTitles(IReadOnlyList<ReleaseCandidate> rows, IndexerConfigDto source, MediaType mediaType)
    {
        var result = new List<RecommendedFeedItemDto>();
        int rank = 0;

        foreach (var row in rows)
        {
            var parsed = parser.Parse(row.ReleaseName);

            // No recognisable resolution usually means a poorly-labelled or junk upload.
            if (parsed.Resolution <= 0) continue;
            // Cams are never something to recommend.
            if (parsed.Source == ReleaseSource.Cam) continue;
            // A title we couldn't parse out has nothing to resolve against TMDB.
            if (string.IsNullOrWhiteSpace(parsed.Title) || parsed.Title.Length < 2) continue;
            // For TV, only take season/episode-tagged rows — otherwise the "title" is often a channel or
            // a release group.
            if (mediaType == MediaType.TvShow && parsed.Season is null && !parsed.IsSeasonPack) continue;

            result.Add(new RecommendedFeedItemDto
            {
                Title = parsed.Title,
                Year = parsed.Year,
                MediaType = mediaType,
                Rank = rank++,
                IndexerId = source.Id
            });
        }
        return result;
    }
}
