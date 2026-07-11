using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// Links a Discord user to a PlexRequests account via a one-time code shown on the web Profile page.
/// Codes live briefly in IMemoryCache; the association is stored on <see cref="Infrastructure.Entities.UserProfileEntity"/>.
/// </summary>
public class DiscordLinkService(AppDbContext db, IMemoryCache cache) : IDiscordLinkService
{
    private const string CachePrefix = "discordlink:";
    private static readonly TimeSpan CodeTtl = TimeSpan.FromMinutes(10);
    // Unambiguous alphabet (no 0/O/1/I) for readable codes.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public string GenerateLinkCode(int userId)
    {
        var code = NewCode(6);
        cache.Set(CachePrefix + code, userId, CodeTtl);
        return code;
    }

    public async Task<BridgeLinkResultDto> CompleteLinkAsync(string code, string discordUserId, string? discordUsername)
    {
        var key = CachePrefix + (code ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(discordUserId) || !cache.TryGetValue(key, out int userId))
            return new BridgeLinkResultDto { Success = false, Message = "That code is invalid or has expired. Generate a new one on your Plex Requests profile." };

        var profile = await db.UserProfiles.Include(p => p.User).FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile is null)
            return new BridgeLinkResultDto { Success = false, Message = "Account not found." };

        // A Discord id maps to at most one account: unlink it from any other profile first.
        var others = await db.UserProfiles.Where(p => p.DiscordUserId == discordUserId && p.UserId != userId).ToListAsync();
        foreach (var o in others) { o.DiscordUserId = null; o.DiscordUsername = null; }

        profile.DiscordUserId = discordUserId;
        profile.DiscordUsername = discordUsername;
        profile.DiscordDmOptIn = true; // opt in on link; user can toggle in Profile
        await db.SaveChangesAsync();
        cache.Remove(key);

        return new BridgeLinkResultDto
        {
            Success = true,
            PlexUsername = profile.PlexUsername ?? profile.User?.Username,
            Message = "Your Discord account is now linked to Plex Requests."
        };
    }

    public async Task<BridgeLinkStatusDto> GetStatusByDiscordIdAsync(string discordUserId)
    {
        var p = await db.UserProfiles.Include(x => x.User).FirstOrDefaultAsync(x => x.DiscordUserId == discordUserId);
        return p is null
            ? new BridgeLinkStatusDto { Linked = false }
            : new BridgeLinkStatusDto { Linked = true, PlexUsername = p.PlexUsername ?? p.User?.Username, DmOptIn = p.DiscordDmOptIn };
    }

    public Task<int?> ResolveUserIdAsync(string discordUserId) =>
        db.UserProfiles.Where(p => p.DiscordUserId == discordUserId).Select(p => (int?)p.UserId).FirstOrDefaultAsync();

    public async Task<bool> IsAdminAsync(string discordUserId)
    {
        var roles = await db.UserProfiles.Where(p => p.DiscordUserId == discordUserId).Select(p => p.Roles).FirstOrDefaultAsync();
        return (roles ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains("Admin", StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> SetDmOptInAsync(int userId, bool optIn)
    {
        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile is null) return false;
        profile.DiscordDmOptIn = optIn;
        await db.SaveChangesAsync();
        return true;
    }

    private static string NewCode(int length)
    {
        var chars = new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }
}
