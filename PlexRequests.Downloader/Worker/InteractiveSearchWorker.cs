using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Api;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Worker;

/// <summary>
/// Serves admin-initiated searches: claim a queued task, search the indexers, evaluate everything with the
/// same logic the automatic search uses, and post it all back — including what it would have rejected and
/// why.
///
/// Runs on its own short poll rather than sharing <c>FulfillmentWorker</c>'s 15-second loop, because a
/// person is waiting on the other end. Long-polling was the alternative and was rejected: it holds a
/// connection open through the VPN tunnel for the duration and collides with the API client's 30-second
/// timeout, for no gain over a cheap GET against a local bridge network.
/// </summary>
public class InteractiveSearchWorker(
    IPlexRequestsApiClient api,
    IIndexerClient indexer,
    IIndexerSettingsProvider indexerSettings,
    IReleaseEvaluator evaluator,
    IDownloadPreferencesProvider prefs,
    IOptions<WorkerOptions> workerOptions,
    ILogger<InteractiveSearchWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private readonly string _workerId = workerOptions.Value.WorkerId ?? Environment.MachineName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting before the first poll.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        logger.LogInformation("Interactive search worker started (polling every {Seconds}s)", PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            bool didWork = false;
            try { didWork = await PollOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Interactive search poll failed"); }

            // Only pause when there was nothing to do, so a queue of searches drains back to back.
            if (!didWork)
            {
                try { await Task.Delay(PollInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        var task = await api.ClaimSearchTaskAsync(_workerId, ct);
        if (task is null) return false;

        // A capabilities probe rides the same queue for the same reason a search does — the web app can't
        // reach an indexer itself without egressing outside the tunnel — but it shares none of the search
        // path, so it branches before any of the ranking setup below.
        if (task.Kind == SearchTaskKind.CapabilitiesTest)
        {
            var caps = task.IndexerId is int indexerId
                ? await indexer.TestAsync(indexerId, ct)
                : new IndexerCapabilitiesDto { Message = "The test didn't name an indexer." };

            logger.LogInformation("Capabilities test #{Id} for indexer {IndexerId}: {Message}",
                task.Id, task.IndexerId, caps.Message);

            await api.CompleteSearchTaskAsync(task.Id,
                new SearchTaskResultDto { Capabilities = caps, Error = caps.Reachable ? null : caps.Message }, ct);
            return true;
        }

        logger.LogInformation("Interactive search #{Id} claimed for \"{Title}\"", task.Id, task.Title);
        var result = new SearchTaskResultDto();

        try
        {
            await prefs.RefreshAsync(ct);
            await indexerSettings.RefreshAsync(ct);

            // An interactive search is scoped by its own toggle: an admin may want a slow or noisy indexer
            // available for a manual look without it slowing every automatic search.
            var job = ToJob(task);
            var search = await indexer.SearchAsync(job, ct);
            result.IndexerSummary = search.Summary;

            var context = new RankingContext
            {
                Preferences = prefs.Current.ToDto(),
                Profile = task.QualityProfile,
                Definitions = task.QualityDefinitions,
                IndexerPriorities = indexerSettings.All.ToDictionary(i => i.Id, i => i.Priority),
                BlocklistedHashes = task.BlocklistedHashes
                    .Select(MagnetUtil.Normalize).Where(h => h is not null).Select(h => h!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
            };

            var ranked = evaluator.EvaluateAll(search.Candidates, job, context);
            result.Results = ranked
                .Where(r => task.IncludeRejected || r.Accepted)
                .Select(ToDto).ToList();

            logger.LogInformation("Interactive search #{Id}: {Accepted} acceptable of {Total} candidate(s)",
                task.Id, result.Results.Count(r => r.Accepted), result.Results.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Interactive search #{Id} failed", task.Id);
            result.Error = ex.Message;
        }

        await api.CompleteSearchTaskAsync(task.Id, result, ct);
        return true;
    }

    /// <summary>Shape the task as a job so it goes through the identical search and evaluation path.</summary>
    private static FulfillmentJobDto ToJob(SearchTaskDto t) => new()
    {
        Id = 0,
        MediaRequestId = t.MediaRequestId ?? 0,
        MediaId = t.MediaId,
        MediaType = t.MediaType,
        Title = t.Title,
        Year = t.Year,
        ImdbId = t.ImdbId,
        TmdbId = t.MediaId,
        // No flat floor: an interactive search deliberately shows everything and lets the profile decide
        // what's acceptable, so the admin can see the releases that were rejected as well as the ones weren't.
        Quality = PlexRequestsHosted.Shared.Enums.Quality.Any,
        QualityProfile = t.QualityProfile,
        QualityDefinitions = t.QualityDefinitions,
        RequestedSeasons = t.Season is int s && t.Episode is null ? new List<int> { s } : new(),
        RequestedEpisodes = t is { Season: int se, Episode: int ep }
            ? new List<EpisodeRef> { new() { Season = se, Episode = ep } }
            : new()
    };

    private static SearchResultDto ToDto(RankedCandidate r) => new()
    {
        ReleaseName = r.Candidate.ReleaseName,
        Magnet = r.Candidate.Magnet,
        InfoHash = MagnetUtil.Normalize(r.Candidate.InfoHash) ?? MagnetUtil.InfoHashFromMagnet(r.Candidate.Magnet),
        IndexerId = r.Candidate.IndexerId,
        IndexerName = r.Candidate.Source,
        Seeders = r.Candidate.Seeders,
        SeedersKnown = r.Candidate.SeedersKnown,
        SizeGb = Math.Round(r.Candidate.SizeGb, 2),
        SizeKnown = r.Candidate.SizeKnown,
        PublishDate = r.Candidate.PublishDate,
        Resolution = r.Resolution,
        Season = r.Season,
        Episode = r.Episode,
        IsPack = r.IsPack,
        Accepted = r.Accepted,
        Rejections = r.Rejections.Select(x => x.Detail).ToList(),
        Score = Math.Round(r.Score, 1),
        ScoreBreakdown = r.ScoreBreakdown
            .GroupBy(c => c.Name)
            .ToDictionary(g => g.Key, g => Math.Round(g.Sum(c => c.Points), 1))
    };
}
