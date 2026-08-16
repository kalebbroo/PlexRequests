using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;

namespace PlexRequestsHosted.Services.Implementations;

public interface IMonitoringPreferencesService
{
    /// <summary>The single monitoring settings row, created on first use.</summary>
    Task<MonitoringPreferencesEntity> GetAsync();
    Task<bool> SaveAsync(MonitoringPreferencesEntity prefs);
}

/// <summary>Singleton-row settings for ongoing-series monitoring, following the same pattern as the
/// download and library preference services.</summary>
public class MonitoringPreferencesService(AppDbContext db) : IMonitoringPreferencesService
{
    public async Task<MonitoringPreferencesEntity> GetAsync()
    {
        var row = await db.MonitoringPreferences.FirstOrDefaultAsync(p => p.IsSingleton);
        if (row is not null) return row;

        row = new MonitoringPreferencesEntity();
        db.MonitoringPreferences.Add(row);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            // Another request created it first; the unique index rejected ours.
            db.Entry(row).State = EntityState.Detached;
            row = await db.MonitoringPreferences.FirstAsync(p => p.IsSingleton);
        }
        return row;
    }

    public async Task<bool> SaveAsync(MonitoringPreferencesEntity prefs)
    {
        var row = await GetAsync();
        row.AssumedAirTimeLocal = prefs.AssumedAirTimeLocal;
        row.AirTimeZoneId = prefs.AirTimeZoneId;
        row.PostAirDelayMinutes = Math.Clamp(prefs.PostAirDelayMinutes, 0, 1440);
        row.MaxSearchAttemptsPerEpisode = Math.Clamp(prefs.MaxSearchAttemptsPerEpisode, 1, 100);
        row.CalendarHorizonDays = Math.Clamp(prefs.CalendarHorizonDays, 1, 365);
        row.CalendarBackfillDays = Math.Clamp(prefs.CalendarBackfillDays, 0, 365);
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }
}
