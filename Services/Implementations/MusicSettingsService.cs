using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

public sealed class MusicSettingsService(AppDbContext db) : IMusicSettingsService
{
    public async Task<MusicSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var row = await GetOrCreateAsync(cancellationToken);
        return new MusicSettingsDto
        {
            CatalogEnabled = row.CatalogEnabled,
            UpdatedAt = row.UpdatedAt
        };
    }

    public async Task UpdateAsync(MusicSettingsDto settings, CancellationToken cancellationToken = default)
    {
        var row = await GetOrCreateAsync(cancellationToken);
        row.CatalogEnabled = settings.CatalogEnabled;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
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
