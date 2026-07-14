using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

public interface IDownloadPreferencesService
{
    /// <summary>Get the global download preferences, lazily creating the default row if none exists.</summary>
    Task<DownloadPreferencesDto> GetAsync();
    /// <summary>Persist edited preferences over the single settings row.</summary>
    Task<bool> UpdateAsync(DownloadPreferencesDto prefs);
}

/// <summary>
/// DB-backed store for the single global <see cref="DownloadPreferencesEntity"/> row, mirroring the
/// default-quality-rule singleton pattern in <see cref="QualityRuleService"/>.
/// </summary>
public class DownloadPreferencesService(AppDbContext db) : IDownloadPreferencesService
{
    public async Task<DownloadPreferencesDto> GetAsync() => ToDto(await GetOrCreateAsync());

    public async Task<bool> UpdateAsync(DownloadPreferencesDto prefs)
    {
        var e = await GetOrCreateAsync();
        e.SeasonPackStrategy = prefs.SeasonPackStrategy;
        e.AllowEpisodeFallback = prefs.AllowEpisodeFallback;
        e.MaxEpisodesForFanout = Math.Clamp(prefs.MaxEpisodesForFanout, 1, 500);
        e.MinSeeders = Math.Max(0, prefs.MinSeeders);
        e.MaxSizeGb = Math.Max(0.05, prefs.MaxSizeGb);
        e.MaxSeasonPackSizeGb = Math.Max(0.05, prefs.MaxSeasonPackSizeGb);
        e.PreferredGroupsCsv = string.IsNullOrWhiteSpace(prefs.PreferredGroupsCsv) ? null : prefs.PreferredGroupsCsv.Trim();
        e.PreferX265 = prefs.PreferX265;
        e.PreferHdr = prefs.PreferHdr;
        e.PreferHigherQualitySource = prefs.PreferHigherQualitySource;
        e.EnforceQualityFloor = prefs.EnforceQualityFloor;
        e.MinTitleSimilarity = Math.Clamp(prefs.MinTitleSimilarity, 0, 1);
        e.AutoMonitorEntireSeriesRequests = prefs.AutoMonitorEntireSeriesRequests;
        e.UpdatedAt = DateTime.UtcNow;
        return await db.SaveChangesAsync() > 0;
    }

    private async Task<DownloadPreferencesEntity> GetOrCreateAsync()
    {
        var e = await db.DownloadPreferences.FirstOrDefaultAsync(x => x.IsSingleton);
        if (e is not null) return e;
        e = new DownloadPreferencesEntity { IsSingleton = true };
        db.DownloadPreferences.Add(e);
        await db.SaveChangesAsync();
        return e;
    }

    private static DownloadPreferencesDto ToDto(DownloadPreferencesEntity e) => new()
    {
        SeasonPackStrategy = e.SeasonPackStrategy,
        AllowEpisodeFallback = e.AllowEpisodeFallback,
        MaxEpisodesForFanout = e.MaxEpisodesForFanout,
        MinSeeders = e.MinSeeders,
        MaxSizeGb = e.MaxSizeGb,
        MaxSeasonPackSizeGb = e.MaxSeasonPackSizeGb,
        PreferredGroupsCsv = e.PreferredGroupsCsv,
        PreferX265 = e.PreferX265,
        PreferHdr = e.PreferHdr,
        PreferHigherQualitySource = e.PreferHigherQualitySource,
        EnforceQualityFloor = e.EnforceQualityFloor,
        MinTitleSimilarity = e.MinTitleSimilarity,
        AutoMonitorEntireSeriesRequests = e.AutoMonitorEntireSeriesRequests
    };
}
