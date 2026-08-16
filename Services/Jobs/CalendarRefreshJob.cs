using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Jobs;

/// <summary>
/// Keeps the air-date calendar current for every monitored series.
///
/// Two things make this different from the blind poll it replaces. It forces a LIVE metadata fetch instead
/// of reading the cache — the cached copy can be 6-12 hours old, which is most of the latency people
/// noticed on newly-aired episodes. And it uses TMDB's own "next episode to air" to refresh only the
/// season that matters, rather than walking every season of every show, which is what makes running this
/// often actually affordable.
/// </summary>
public class CalendarRefreshJob(
    AppDbContext db,
    IMetadataRefreshCoordinator refresh,
    ISeasonAvailabilityEvaluator seasonEvaluator,
    IMonitoringPreferencesService monitoring,
    IConfiguration config,
    ILogger<CalendarRefreshJob> logger) : IJobHandler
{
    public JobType Type => JobType.CalendarRefresh;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        if (!config.GetValue("Monitoring:Enabled", true)) return JobResult.Skipped("Monitoring is disabled");

        var prefs = await monitoring.GetAsync();
        var anchors = await db.MediaRequests
            .Where(r => r.MonitorMode != MonitorMode.None && r.MediaType == MediaType.TvShow)
            .ToListAsync(ct);
        if (anchors.Count == 0) return JobResult.Skipped("No series are being monitored");

        int rows = 0;
        foreach (var anchor in anchors)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                rows += await RefreshSeriesAsync(anchor, prefs, ct);
                // Saved per anchor, not batched to the end. A show is routinely monitored by more than one
                // MediaRequest row at once (a whole-series monitor plus per-episode children, or two
                // differently-scoped monitors), and each one calls RefreshSeriesAsync in turn. The "existing"
                // lookup inside it queries the database, which does NOT see another anchor's Added-but-
                // unsaved rows from earlier in this same loop — so two anchors for the same show would both
                // decide the same (season, episode) was new and add it twice, and a single SaveChanges at the
                // end would fail the whole pass on that show's unique-constraint violation, silently losing
                // every other show's refresh in the same run. Saving here means the second anchor's query
                // sees the first anchor's rows and updates them instead of duplicating them, and one show's
                // failure can no longer take the rest of the pass down with it.
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Calendar refresh failed for \"{Title}\"", anchor.Title); }
        }

        return rows > 0
            ? JobResult.Ok(rows, $"Refreshed {rows} calendar entry(ies) across {anchors.Count} series")
            : JobResult.Skipped("Nothing changed in the calendar");
    }

    private async Task<int> RefreshSeriesAsync(MediaRequestEntity anchor, MonitoringPreferencesEntity prefs, CancellationToken ct)
    {
        // Live, not cached. This is the fix for "a new episode took most of a day to be noticed".
        var detail = await refresh.RefreshDetailNowAsync(MediaType.TvShow, anchor.MediaId);
        if (detail is null) return 0;

        anchor.LastMonitorCheckAt = DateTime.UtcNow;

        // Which seasons to look at. Normally just the one containing the next episode — walking all of them
        // every pass is what made a frequent refresh unaffordable. Fall back to the full list when TMDB
        // doesn't tell us (an ended show, or a gap between seasons).
        var seasons = new List<int>();
        if (detail.NextEpisodeSeason is int next) seasons.Add(next);
        if (seasons.Count == 0 || anchor.AutoMonitorNewSeasons)
            seasons.AddRange(detail.Seasons.Where(s => s.SeasonNumber > 0).Select(s => s.SeasonNumber));
        seasons = seasons.Distinct().ToList();
        if (seasons.Count == 0) return 0;

        var onPlex = await seasonEvaluator.GetPlexEpisodesAsync(anchor.MediaId, ct);
        var existing = await db.AirSchedule.Where(a => a.ShowTmdbId == anchor.MediaId).ToListAsync(ct);
        var byKey = existing.ToDictionary(a => (a.SeasonNumber, a.EpisodeNumber));

        var horizon = DateTime.UtcNow.AddDays(prefs.CalendarHorizonDays);
        int touched = 0;

        foreach (var seasonNumber in seasons)
        {
            if (ct.IsCancellationRequested) break;
            var episodes = await refresh.RefreshEpisodesNowAsync(anchor.MediaId, seasonNumber);
            if (episodes.Count == 0) continue;

            var have = onPlex.TryGetValue(seasonNumber, out var set) ? set : new HashSet<int>();

            foreach (var ep in episodes)
            {
                if (ep.AirDate is DateTime d && d > horizon) continue; // too far out to be worth a row yet

                if (!byKey.TryGetValue((seasonNumber, ep.EpisodeNumber), out var row))
                {
                    row = new AirScheduleEntity
                    {
                        ShowTmdbId = anchor.MediaId,
                        SeasonNumber = seasonNumber,
                        EpisodeNumber = ep.EpisodeNumber
                    };
                    db.AirSchedule.Add(row);
                    byKey[(seasonNumber, ep.EpisodeNumber)] = row;
                }

                row.EpisodeTitle = ep.Name;
                row.AirDate = ep.AirDate;
                row.MediaRequestId = anchor.Id;
                row.HasFile = have.Contains(ep.EpisodeNumber);
                row.Monitored = WantsEpisode(anchor, seasonNumber, ep.EpisodeNumber, row.HasFile, ep.AirDate, seasons);
                row.AirsAtUtc = ComputeAirsAt(ep.AirDate, anchor, prefs);
                ApplyState(row, prefs);
                row.UpdatedAt = DateTime.UtcNow;
                touched++;
            }
        }
        return touched;
    }

    /// <summary>Whether the request's monitor mode wants this particular episode.</summary>
    private static bool WantsEpisode(MediaRequestEntity anchor, int season, int episode, bool hasFile, DateTime? airDate, List<int> allSeasons) =>
        anchor.MonitorMode switch
        {
            MonitorMode.None => false,
            MonitorMode.AllEpisodes => !hasFile,
            MonitorMode.MissingEpisodes => !hasFile,
            // "Future" means from the moment monitoring started, not from the show's beginning.
            MonitorMode.FutureEpisodes => !hasFile && airDate is DateTime d
                                          && d >= (anchor.MonitoredSince ?? anchor.RequestedAt).Date,
            MonitorMode.ExistingEpisodes => hasFile,   // upgrades only
            MonitorMode.FirstSeason => !hasFile && season == allSeasons.Min(),
            MonitorMode.LatestSeason => !hasFile && season == allSeasons.Max(),
            _ => false
        };

    /// <summary>
    /// When to start looking. TMDB gives a date and no time, so this is an assumption: the configured local
    /// broadcast time (or a per-series override) plus a delay for releases to actually appear. Deliberately
    /// approximate — release monitoring is what catches anything this misses.
    /// </summary>
    internal static DateTime? ComputeAirsAt(DateTime? airDate, MediaRequestEntity anchor, MonitoringPreferencesEntity prefs)
    {
        if (airDate is not DateTime date) return null;

        var minutes = anchor.AirTimeOverrideMinutes ?? ParseTime(prefs.AssumedAirTimeLocal);
        var localAir = date.Date.AddMinutes(minutes);

        DateTime utc;
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(prefs.AirTimeZoneId);
            utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localAir, DateTimeKind.Unspecified), tz);
        }
        catch (Exception)
        {
            // An unknown timezone id must not stop the calendar working; treat the assumed time as UTC.
            utc = DateTime.SpecifyKind(localAir, DateTimeKind.Utc);
        }
        return utc.AddMinutes(prefs.PostAirDelayMinutes);
    }

    private static int ParseTime(string hhmm)
    {
        var parts = (hhmm ?? "20:00").Split(':');
        return parts.Length == 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m)
            ? Math.Clamp(h, 0, 23) * 60 + Math.Clamp(m, 0, 59)
            : 20 * 60;
    }

    /// <summary>Derive the search state and next wake time from what we now know about the episode.</summary>
    internal static void ApplyState(AirScheduleEntity row, MonitoringPreferencesEntity prefs)
    {
        if (row.HasFile) { row.SearchState = AirSearchState.Acquired; row.NextSearchAt = null; return; }
        if (!row.Monitored) { row.SearchState = AirSearchState.Skipped; row.NextSearchAt = null; return; }
        if (row.SearchAttempts >= prefs.MaxSearchAttemptsPerEpisode)
        {
            row.SearchState = AirSearchState.Skipped;
            row.NextSearchAt = null;
            return;
        }

        if (row.AirsAtUtc is not DateTime airsAt)
        {
            // No air date at all. Previously these were skipped forever, because "has aired" is false
            // without a date. Give them a slow sweep instead — undated episodes are common for anime and
            // for same-day releases, and they do eventually show up.
            row.SearchState = AirSearchState.AirDateUnknown;
            row.NextSearchAt ??= DateTime.UtcNow.AddDays(7);
            return;
        }

        if (airsAt > DateTime.UtcNow)
        {
            row.SearchState = AirSearchState.NotDue;
            row.NextSearchAt = airsAt;
        }
        else if (row.SearchState != AirSearchState.Searching)
        {
            row.SearchState = AirSearchState.Due;
            row.NextSearchAt ??= DateTime.UtcNow;
        }
    }
}
