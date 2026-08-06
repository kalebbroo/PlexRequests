using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

public interface ISeriesMonitoringService
{
    /// <summary>Monitoring state for a show, or null when nothing is requesting it.</summary>
    Task<SeriesMonitoringDto?> GetAsync(int showTmdbId);
    Task<bool> SetModeAsync(int requestId, MonitorMode mode);
    Task<bool> SetFlagsAsync(int requestId, bool autoMonitorNewSeasons, bool searchForCutoffUpgrades);
    /// <summary>The show's calendar, newest season first.</summary>
    Task<List<AirScheduleEntryDto>> GetScheduleAsync(int showTmdbId);
    /// <summary>The next 30 days across every monitored show, for the admin calendar.</summary>
    Task<List<AirScheduleEntryDto>> GetUpcomingAsync(int days = 30);
}

/// <summary>
/// Read/write model behind the per-series monitoring panel.
///
/// This is the first way anything in the app can change whether a series is monitored: the flag was written
/// once at request creation and never again, so a user who wanted a show followed — or wanted it to stop —
/// had no recourse short of editing the database.
/// </summary>
public class SeriesMonitoringService(AppDbContext db, ILogger<SeriesMonitoringService> logger) : ISeriesMonitoringService
{
    public async Task<SeriesMonitoringDto?> GetAsync(int showTmdbId)
    {
        // The anchor is the broadest live request for the show — a whole-series request outranks a
        // season-scoped one, since that's what "monitor this show" naturally attaches to.
        var anchor = await db.MediaRequests
            .Where(r => r.MediaId == showTmdbId && r.MediaType == MediaType.TvShow
                        && r.Status != RequestStatus.Cancelled && r.Status != RequestStatus.Rejected)
            .OrderByDescending(r => r.RequestAllSeasons)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync();
        if (anchor is null) return null;

        return new SeriesMonitoringDto
        {
            RequestId = anchor.Id,
            ShowTmdbId = anchor.MediaId,
            Title = anchor.Title,
            MonitorMode = anchor.MonitorMode,
            AutoMonitorNewSeasons = anchor.AutoMonitorNewSeasons,
            SearchForCutoffUpgrades = anchor.SearchForCutoffUpgrades,
            QualityProfileId = anchor.QualityProfileId,
            MonitoredSince = anchor.MonitoredSince,
            LastCheckedAt = anchor.LastMonitorCheckAt
        };
    }

    public async Task<bool> SetModeAsync(int requestId, MonitorMode mode)
    {
        var r = await db.MediaRequests.FirstOrDefaultAsync(x => x.Id == requestId);
        if (r is null) return false;

        r.MonitorMode = mode;
        r.Monitored = mode != MonitorMode.None;      // legacy mirror
        if (mode != MonitorMode.None) r.MonitoredSince ??= DateTime.UtcNow;

        // Turning monitoring off must also stand down the calendar, or the air-date job keeps queueing
        // episodes for a series nobody is following any more.
        if (mode == MonitorMode.None)
        {
            await db.AirSchedule.Where(a => a.MediaRequestId == requestId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Monitored, false)
                    .SetProperty(a => a.SearchState, AirSearchState.Skipped)
                    .SetProperty(a => a.NextSearchAt, (DateTime?)null));
        }
        else
        {
            // Re-enabling: let the next calendar refresh re-derive what's wanted rather than guessing here.
            r.LastMonitorCheckAt = null;
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Monitoring for \"{Title}\" set to {Mode}", r.Title, mode);
        return true;
    }

    public async Task<bool> SetFlagsAsync(int requestId, bool autoMonitorNewSeasons, bool searchForCutoffUpgrades)
    {
        var r = await db.MediaRequests.FirstOrDefaultAsync(x => x.Id == requestId);
        if (r is null) return false;
        r.AutoMonitorNewSeasons = autoMonitorNewSeasons;
        r.SearchForCutoffUpgrades = searchForCutoffUpgrades;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<List<AirScheduleEntryDto>> GetScheduleAsync(int showTmdbId) =>
        await Project(db.AirSchedule.Where(a => a.ShowTmdbId == showTmdbId))
            .OrderByDescending(a => a.Season).ThenBy(a => a.Episode)
            .ToListAsync();

    public async Task<List<AirScheduleEntryDto>> GetUpcomingAsync(int days = 30)
    {
        var from = DateTime.UtcNow.Date.AddDays(-1);
        var to = DateTime.UtcNow.Date.AddDays(Math.Clamp(days, 1, 120));
        return await Project(db.AirSchedule.Where(a => a.AirDate != null && a.AirDate >= from && a.AirDate <= to))
            .OrderBy(a => a.AirDate).ThenBy(a => a.Title).ToListAsync();
    }

    private IQueryable<AirScheduleEntryDto> Project(IQueryable<Infrastructure.Entities.AirScheduleEntity> q) =>
        from a in q
        join r in db.MediaRequests on a.MediaRequestId equals r.Id into rj
        from r in rj.DefaultIfEmpty()
        select new AirScheduleEntryDto
        {
            Id = a.Id,
            ShowTmdbId = a.ShowTmdbId,
            Title = r != null ? r.Title : "(unknown)",
            MediaRequestId = a.MediaRequestId,
            Season = a.SeasonNumber,
            Episode = a.EpisodeNumber,
            EpisodeTitle = a.EpisodeTitle,
            AirDate = a.AirDate,
            AirsAtUtc = a.AirsAtUtc,
            State = a.SearchState,
            Monitored = a.Monitored,
            HasFile = a.HasFile,
            NextSearchAt = a.NextSearchAt,
            SearchAttempts = a.SearchAttempts
        };
}
