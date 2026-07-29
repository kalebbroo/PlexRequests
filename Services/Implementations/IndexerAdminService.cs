using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// Database-backed <see cref="IIndexerAdminService"/>. Rows are seeded for the built-in providers so the
/// panel isn't empty before the downloader's first search, and any provider name the downloader reports
/// that isn't known yet is auto-registered — adding a new indexer to the downloader needs no web change.
/// </summary>
public class IndexerAdminService(AppDbContext db, ILogger<IndexerAdminService> logger) : IIndexerAdminService
{
    // Pre-seeded so admins can configure providers before any search has run. Must match the
    // downloader providers' Name values; Torznab endpoints report as "Torznab" (rolled up).
    private static readonly string[] BuiltIns = { "EZTV", "YTS", "1337x", "Nyaa", "ext.to", "PirateBay", "TorrentsCSV", "Torznab" };

    public async Task<List<IndexerSettingDto>> GetAllAsync()
    {
        await SeedAsync();
        return await db.IndexerSettings
            .OrderBy(i => i.Priority).ThenBy(i => i.Name)
            .Select(i => new IndexerSettingDto
            {
                Name = i.Name, Enabled = i.Enabled, Priority = i.Priority,
                LastSearchAt = i.LastSearchAt, LastResultCount = i.LastResultCount,
                LastSuccessAt = i.LastSuccessAt, LastError = i.LastError,
                ConsecutiveFailures = i.ConsecutiveFailures,
                TotalSearches = i.TotalSearches, TotalResults = i.TotalResults,
                LastLatencyMs = i.LastLatencyMs
            }).ToListAsync();
    }

    public async Task<bool> SetEnabledAsync(string name, bool enabled)
    {
        var row = await db.IndexerSettings.FirstOrDefaultAsync(i => i.Name == name);
        if (row is null) return false;
        row.Enabled = enabled;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        logger.LogInformation("Indexer \"{Name}\" {State} by admin", name, enabled ? "enabled" : "disabled");
        return true;
    }

    public async Task<bool> SetPriorityAsync(string name, int priority)
    {
        var row = await db.IndexerSettings.FirstOrDefaultAsync(i => i.Name == name);
        if (row is null) return false;
        row.Priority = Math.Clamp(priority, 1, 50);
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<List<IndexerConfigDto>> GetWorkerConfigAsync()
    {
        await SeedAsync();
        return await db.IndexerSettings
            .Select(i => new IndexerConfigDto { Name = i.Name, Enabled = i.Enabled, Priority = i.Priority })
            .ToListAsync();
    }

    public async Task ReportStatusAsync(List<IndexerStatusReportDto> reports)
    {
        if (reports.Count == 0) return;
        var now = DateTime.UtcNow;
        var names = reports.Select(r => r.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        var rows = await db.IndexerSettings.Where(i => names.Contains(i.Name)).ToDictionaryAsync(i => i.Name);

        foreach (var r in reports)
        {
            if (string.IsNullOrWhiteSpace(r.Name)) continue;
            if (!rows.TryGetValue(r.Name, out var row))
            {
                row = new IndexerSettingEntity { Name = r.Name, CreatedAt = now };
                db.IndexerSettings.Add(row);
                rows[r.Name] = row;
            }

            row.LastSearchAt = r.SearchedAt == default ? now : r.SearchedAt;
            row.LastResultCount = r.ResultCount;
            row.LastLatencyMs = r.LatencyMs;
            row.TotalSearches++;
            row.TotalResults += r.ResultCount;
            if (r.Success)
            {
                row.LastSuccessAt = row.LastSearchAt;
                row.ConsecutiveFailures = 0;
                row.LastError = null;
            }
            else
            {
                row.ConsecutiveFailures++;
                row.LastError = r.Error is { Length: > 512 } e ? e[..512] : r.Error;
            }
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync();
    }

    private async Task SeedAsync()
    {
        var existing = await db.IndexerSettings.Select(i => i.Name).ToListAsync();
        var now = DateTime.UtcNow;
        foreach (var name in BuiltIns.Where(n => !existing.Contains(n)))
            db.IndexerSettings.Add(new IndexerSettingEntity { Name = name, CreatedAt = now, UpdatedAt = now });
        if (!db.ChangeTracker.HasChanges()) return;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            // Two callers seeded concurrently and the unique Name index rejected the duplicate — the rows
            // exist either way, so drop ours and carry on.
            foreach (var entry in db.ChangeTracker.Entries<IndexerSettingEntity>().Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
        }
    }
}
