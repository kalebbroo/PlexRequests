using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>Resolves provider ids into one canonical row and keeps alias writes idempotent.</summary>
public sealed class MediaIdentityService(AppDbContext db) : IMediaIdentityService
{
    public async Task<MediaIdentityEntity> ResolveAsync(MediaRef mediaRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaRef);
        if (!mediaRef.IsValid) throw new ArgumentException("A valid provider, id, media type and kind are required.", nameof(mediaRef));

        var provider = MediaRef.NormalizeProvider(mediaRef.Provider);
        var externalId = MediaRef.NormalizeId(provider, mediaRef.Id);
        var alias = await db.MediaExternalIdentifiers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.MediaType == mediaRef.MediaType && x.Kind == mediaRef.Kind
                                      && x.Provider == provider && x.ExternalId == externalId,
                cancellationToken);
        if (alias is not null)
            return await db.MediaIdentities.FirstAsync(x => x.Id == alias.MediaIdentityId, cancellationToken);

        var stableKey = mediaRef with { Provider = provider, Id = externalId };
        var existing = await db.MediaIdentities.FirstOrDefaultAsync(x => x.StableKey == stableKey.StableKey,
            cancellationToken);
        if (existing is not null)
        {
            await EnsureAliasAsync(existing, provider, externalId, mediaRef, cancellationToken);
            return existing;
        }

        var now = DateTime.UtcNow;
        var entity = new MediaIdentityEntity
        {
            MediaType = mediaRef.MediaType,
            Kind = mediaRef.Kind,
            PrimaryProvider = provider,
            PrimaryId = externalId,
            StableKey = stableKey.StableKey,
            CreatedAt = now,
            UpdatedAt = now
        };
        entity.ExternalIdentifiers.Add(new MediaExternalIdentifierEntity
        {
            MediaType = mediaRef.MediaType,
            Kind = mediaRef.Kind,
            Provider = provider,
            ExternalId = externalId,
            IsPrimary = true
        });
        db.MediaIdentities.Add(entity);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return entity;
        }
        catch (DbUpdateException)
        {
            // Another request may have resolved the same provider id between our read and insert.
            db.Entry(entity).State = EntityState.Detached;
            foreach (var child in entity.ExternalIdentifiers) db.Entry(child).State = EntityState.Detached;
            var winner = await db.MediaIdentities.FirstOrDefaultAsync(x => x.StableKey == stableKey.StableKey,
                cancellationToken);
            if (winner is not null) return winner;
            throw;
        }
    }

    public async Task<MediaRef?> GetRefAsync(int mediaIdentityId, CancellationToken cancellationToken = default)
    {
        var entity = await db.MediaIdentities.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == mediaIdentityId, cancellationToken);
        return entity is null ? null : MediaRef.FromExternal(entity.PrimaryProvider, entity.PrimaryId,
            entity.MediaType, entity.Kind);
    }

    public async Task AddAliasAsync(int mediaIdentityId, MediaRef alias, CancellationToken cancellationToken = default)
    {
        if (!alias.IsValid) throw new ArgumentException("Alias is invalid.", nameof(alias));
        var entity = await db.MediaIdentities.FirstOrDefaultAsync(x => x.Id == mediaIdentityId, cancellationToken)
                     ?? throw new KeyNotFoundException($"Media identity {mediaIdentityId} was not found.");
        if (entity.MediaType != alias.MediaType || entity.Kind != alias.Kind)
            throw new InvalidOperationException("An alias must describe the same media type and kind as its identity.");
        var provider = MediaRef.NormalizeProvider(alias.Provider);
        await EnsureAliasAsync(entity, provider, MediaRef.NormalizeId(provider, alias.Id), alias, cancellationToken);
    }

    private async Task EnsureAliasAsync(MediaIdentityEntity entity, string provider, string externalId,
        MediaRef alias, CancellationToken cancellationToken)
    {
        var exists = await db.MediaExternalIdentifiers.AnyAsync(x => x.MediaType == alias.MediaType
            && x.Kind == alias.Kind
            && x.Provider == provider && x.ExternalId == externalId, cancellationToken);
        if (exists) return;
        db.MediaExternalIdentifiers.Add(new MediaExternalIdentifierEntity
        {
            MediaIdentityId = entity.Id,
            MediaType = alias.MediaType,
            Kind = alias.Kind,
            Provider = provider,
            ExternalId = externalId
        });
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}
