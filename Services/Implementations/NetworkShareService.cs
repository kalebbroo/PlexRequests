using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Background;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

public interface INetworkShareService
{
    /// <summary>All configured shares (passwords never included), annotated with live mount status.</summary>
    Task<List<NetworkShareDto>> GetAllAsync();
    Task<NetworkShareDto> CreateAsync(NetworkShareEditDto dto);
    /// <summary>Update a share. A null password keeps the existing one; empty string clears it.</summary>
    Task<bool> UpdateAsync(int id, NetworkShareEditDto dto);
    Task<bool> DeleteAsync(int id);
    /// <summary>Enabled shares with DECRYPTED credentials, for mounting. Never expose to a browser.</summary>
    Task<List<NetworkShareMountDto>> GetMountConfigsAsync();
}

/// <summary>
/// DB-backed CRUD for <see cref="NetworkShareEntity"/>. Encrypts the share password at rest with the
/// app's DataProtection key ring (the same keys persisted to the ./keys volume as auth cookies), and
/// only ever decrypts it server-side to build a mount command or to serve the downloader's secured
/// mount-config endpoint. Mount status shown in the UI comes from the web app's own mount service via
/// <see cref="INetworkMountStatusStore"/>.
/// </summary>
public class NetworkShareService(AppDbContext db, IDataProtectionProvider dp, INetworkMountStatusStore status)
    : INetworkShareService
{
    private readonly IDataProtector _protector = dp.CreateProtector("NetworkShareCredentials.v1");

    public async Task<List<NetworkShareDto>> GetAllAsync()
    {
        var rows = await db.NetworkShares.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
        return rows.Select(ToDto).ToList();
    }

    public async Task<NetworkShareDto> CreateAsync(NetworkShareEditDto dto)
    {
        var existing = await db.NetworkShares.Select(x => x.MountSlug).ToListAsync();
        var e = new NetworkShareEntity
        {
            Name = (dto.Name ?? string.Empty).Trim(),
            MountSlug = UniqueSlug(dto.Name, existing),
            Protocol = dto.Protocol,
            Server = (dto.Server ?? string.Empty).Trim(),
            ShareName = (dto.ShareName ?? string.Empty).Trim(),
            Domain = string.IsNullOrWhiteSpace(dto.Domain) ? null : dto.Domain.Trim(),
            Username = string.IsNullOrWhiteSpace(dto.Username) ? null : dto.Username.Trim(),
            PasswordProtected = string.IsNullOrEmpty(dto.Password) ? null : _protector.Protect(dto.Password),
            Enabled = dto.Enabled,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.NetworkShares.Add(e);
        await db.SaveChangesAsync();
        return ToDto(e);
    }

    public async Task<bool> UpdateAsync(int id, NetworkShareEditDto dto)
    {
        var e = await db.NetworkShares.FirstOrDefaultAsync(x => x.Id == id);
        if (e is null) return false;

        e.Name = (dto.Name ?? string.Empty).Trim();
        e.Protocol = dto.Protocol;
        e.Server = (dto.Server ?? string.Empty).Trim();
        e.ShareName = (dto.ShareName ?? string.Empty).Trim();
        e.Domain = string.IsNullOrWhiteSpace(dto.Domain) ? null : dto.Domain.Trim();
        e.Username = string.IsNullOrWhiteSpace(dto.Username) ? null : dto.Username.Trim();
        e.Enabled = dto.Enabled;
        // Null password = leave as-is (the UI never sends it back); empty = explicitly clear it.
        if (dto.Password is not null)
            e.PasswordProtected = dto.Password.Length == 0 ? null : _protector.Protect(dto.Password);
        e.UpdatedAt = DateTime.UtcNow;

        return await db.SaveChangesAsync() > 0;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var e = await db.NetworkShares.FirstOrDefaultAsync(x => x.Id == id);
        if (e is null) return false;
        db.NetworkShares.Remove(e);
        return await db.SaveChangesAsync() > 0;
    }

    public async Task<List<NetworkShareMountDto>> GetMountConfigsAsync()
    {
        var rows = await db.NetworkShares.AsNoTracking().Where(x => x.Enabled).ToListAsync();
        return rows.Select(e => new NetworkShareMountDto
        {
            MountSlug = e.MountSlug,
            Protocol = e.Protocol,
            Server = e.Server,
            ShareName = e.ShareName,
            Domain = e.Domain,
            Username = e.Username,
            Password = Unprotect(e.PasswordProtected),
            Enabled = e.Enabled
        }).ToList();
    }

    private string? Unprotect(string? blob)
    {
        if (string.IsNullOrEmpty(blob)) return null;
        try { return _protector.Unprotect(blob); }
        catch { return null; } // key rotated / corrupt — treat as no password rather than throwing
    }

    private NetworkShareDto ToDto(NetworkShareEntity e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        MountSlug = e.MountSlug,
        Protocol = e.Protocol,
        Server = e.Server,
        ShareName = e.ShareName,
        Domain = e.Domain,
        Username = e.Username,
        HasPassword = !string.IsNullOrEmpty(e.PasswordProtected),
        Enabled = e.Enabled,
        ServerIsPrivate = NetworkShareServerHelper.LooksPrivate(e.Server),
        Status = status.Get(e.MountSlug)
    };

    /// <summary>Slugify the name (lowercase, alphanumerics + single dashes) and de-duplicate.</summary>
    internal static string UniqueSlug(string? name, ICollection<string> taken)
    {
        var sb = new StringBuilder();
        var lastDash = false;
        foreach (var ch in (name ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); lastDash = false; }
            else if (!lastDash) { sb.Append('-'); lastDash = true; }
        }
        var baseSlug = sb.ToString().Trim('-');
        if (string.IsNullOrEmpty(baseSlug)) baseSlug = "share";

        var slug = baseSlug;
        var n = 2;
        while (taken.Contains(slug)) slug = $"{baseSlug}-{n++}";
        return slug;
    }
}
