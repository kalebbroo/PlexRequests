using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

public sealed class MusicSettingsService(AppDbContext db) : IMusicSettingsService
{
    private readonly object _cacheLock = new();
    private Task<MusicSettingsDto>? _cached;
    private DateTime _cachedAt;

    public Task<MusicSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_cacheLock)
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(2)) return _cached;
            _cachedAt = DateTime.UtcNow;
            return _cached = LoadAsync(CancellationToken.None);
        }
    }

    private async Task<MusicSettingsDto> LoadAsync(CancellationToken cancellationToken)
    {
        var row = await GetOrCreateAsync(cancellationToken);
        var issues = await GetReadinessIssuesAsync(cancellationToken);
        return new MusicSettingsDto
        {
            CatalogEnabled = row.CatalogEnabled,
            RequestsEnabled = row.RequestsEnabled && issues.Count == 0,
            ReadinessIssues = issues,
            UpdatedAt = row.UpdatedAt
        };
    }

    public async Task UpdateAsync(MusicSettingsDto settings, CancellationToken cancellationToken = default)
    {
        var row = await GetOrCreateAsync(cancellationToken);
        var issues = await GetReadinessIssuesAsync(cancellationToken);
        if (settings.RequestsEnabled && issues.Count > 0)
            throw new InvalidOperationException(string.Join(" ", issues));
        row.CatalogEnabled = settings.CatalogEnabled;
        row.RequestsEnabled = settings.RequestsEnabled;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        lock (_cacheLock) { _cached = null; _cachedAt = default; }
    }

    private async Task<List<string>> GetReadinessIssuesAsync(CancellationToken cancellationToken)
    {
        var issues = new List<string>();
        var musicPath = await db.LibraryOrganizationPreferences.AsNoTracking()
            .Where(x => x.IsSingleton).Select(x => x.MusicPath).FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(musicPath))
            issues.Add("Configure a Music library path under Library → Organization.");

        var hasIndexer = await db.IndexerMediaCapabilities.AsNoTracking().AnyAsync(c =>
            c.MediaType == Shared.Enums.MediaType.Music && c.Enabled && c.Indexer != null
            && c.Indexer.Enabled && c.Indexer.EnableAutomaticSearch, cancellationToken);
        if (!hasIndexer)
            issues.Add("Enable Music automatic search on at least one indexer.");
        return issues;
    }

    private async Task<MusicSettingsEntity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var row = await db.MusicSettings.FirstOrDefaultAsync(x => x.IsSingleton, cancellationToken);
        if (row is not null) return row;
        row = new MusicSettingsEntity();
        db.MusicSettings.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return row;
        }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            return await db.MusicSettings.SingleAsync(x => x.IsSingleton, cancellationToken);
        }
    }
}
