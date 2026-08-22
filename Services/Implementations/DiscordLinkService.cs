using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// Links a Discord user to a PlexRequests account via a one-time code shown on the web Profile page.
/// Codes live briefly in IMemoryCache; the association is stored on <see cref="Infrastructure.Entities.UserProfileEntity"/>.
/// </summary>
public class DiscordLinkService(
    AppDbContext db,
    IMemoryCache cache,
    ILogger<DiscordLinkService> logger,
    IUserAccessService userAccess) : IDiscordLinkService
{
    private const string CachePrefix = "discordlink:";
    private static readonly TimeSpan CodeTtl = TimeSpan.FromMinutes(10);
    // Unambiguous alphabet (no 0/O/1/I) for readable codes.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public async Task<string?> GenerateLinkCodeAsync()
    {
        var userId = await userAccess.GetCurrentUserIdAsync();
        if (userId is null) return null;
        var code = NewCode(6);
        cache.Set(CachePrefix + code, new LinkTicket(userId.Value), CodeTtl);
        return code;
    }

    public async Task<BridgeLinkResultDto> CompleteLinkAsync(string code, string discordUserId, string? discordUsername)
    {
        var normalizedCode = (code ?? string.Empty).Trim().ToUpperInvariant();
        var normalizedDiscordId = discordUserId?.Trim() ?? string.Empty;
        var normalizedUsername = discordUsername?.Trim();
        var key = CachePrefix + normalizedCode;
        if (!IsValidDiscordUserId(normalizedDiscordId))
            return new BridgeLinkResultDto { Success = false, Message = "The Discord user id is invalid." };
        if (normalizedUsername?.Length > 128)
            return new BridgeLinkResultDto { Success = false, Message = "The Discord username is too long to store." };
        if (!IsValidCode(normalizedCode) || !cache.TryGetValue(key, out LinkTicket? ticket) || ticket is null
            || !ticket.TryConsume(out var userId))
            return new BridgeLinkResultDto { Success = false, Message = "That code is invalid or has expired. Generate a new one on your Plex Requests profile." };
        cache.Remove(key);

        var profile = await db.UserProfiles.Include(p => p.User).FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile is null)
            return new BridgeLinkResultDto { Success = false, Message = "Account not found." };
        var access = await userAccess.GetAccessAsync(userId);
        if (!access.IsActive)
            return new BridgeLinkResultDto
            {
                Success = false,
                Message = access.AccountStatus == UserAccountStatus.Suspended
                    ? "This Plex Requests account is temporarily suspended."
                    : "This Plex Requests account is disabled."
            };

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            // The database unique index is the final guard. Explicitly unlinking inside the same write
            // transaction makes a deliberate re-link atomic instead of briefly mapping one Discord id twice.
            await db.UserProfiles
                .Where(p => p.DiscordUserId == normalizedDiscordId && p.UserId != userId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.DiscordUserId, (string?)null)
                    .SetProperty(p => p.DiscordUsername, (string?)null)
                    .SetProperty(p => p.DiscordDmOptIn, false));

            profile.DiscordUserId = normalizedDiscordId;
            profile.DiscordUsername = normalizedUsername;
            profile.DiscordDmOptIn = true; // opt in on link; user can toggle in Profile
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            logger.LogWarning(ex, "Discord account linking failed for Plex Requests user {UserId}", userId);
            return new BridgeLinkResultDto
            {
                Success = false,
                Message = "The account could not be linked safely. Generate a new code and try again."
            };
        }

        return new BridgeLinkResultDto
        {
            Success = true,
            PlexUsername = profile.PlexUsername ?? profile.User?.Username,
            Message = "Your Discord account is now linked to Plex Requests."
        };
    }

    public async Task<BridgeLinkStatusDto> GetStatusByDiscordIdAsync(string discordUserId)
    {
        var normalized = discordUserId?.Trim() ?? string.Empty;
        if (!IsValidDiscordUserId(normalized)) return new BridgeLinkStatusDto { Linked = false };
        var p = await db.UserProfiles.Include(x => x.User).FirstOrDefaultAsync(x => x.DiscordUserId == normalized);
        if (p is null) return new BridgeLinkStatusDto { Linked = false };
        var access = await userAccess.GetAccessAsync(p.UserId);
        return new BridgeLinkStatusDto
        {
            Linked = true,
            PlexUsername = p.PlexUsername ?? p.User?.Username,
            DmOptIn = p.DiscordDmOptIn && access.IsActive,
            AccountAvailable = access.IsActive,
            AccountStatus = access.AccountStatus,
            Message = access.IsActive ? null : access.AccountStatus == UserAccountStatus.Suspended
                ? "The linked Plex Requests account is temporarily suspended."
                : "The linked Plex Requests account is disabled."
        };
    }

    public async Task<DiscordLinkStatusDto> GetCurrentStatusAsync()
    {
        var userId = await userAccess.GetCurrentUserIdAsync();
        if (userId is null) return new DiscordLinkStatusDto();

        return await db.UserProfiles.AsNoTracking()
            .Where(p => p.UserId == userId.Value)
            .Select(p => new DiscordLinkStatusDto
            {
                Linked = p.DiscordUserId != null && p.DiscordUserId != string.Empty,
                DiscordUsername = p.DiscordUsername,
                DmOptIn = p.DiscordUserId != null && p.DiscordUserId != string.Empty && p.DiscordDmOptIn
            })
            .FirstOrDefaultAsync() ?? new DiscordLinkStatusDto();
    }

    public Task<int?> ResolveUserIdAsync(string discordUserId)
    {
        var normalized = discordUserId?.Trim() ?? string.Empty;
        return !IsValidDiscordUserId(normalized)
            ? Task.FromResult<int?>(null)
            : db.UserProfiles.Where(p => p.DiscordUserId == normalized)
                .Select(p => (int?)p.UserId).FirstOrDefaultAsync();
    }

    public async Task<bool> IsAdminAsync(string discordUserId)
    {
        var normalized = discordUserId?.Trim() ?? string.Empty;
        if (!IsValidDiscordUserId(normalized)) return false;
        var userId = await db.UserProfiles.Where(p => p.DiscordUserId == normalized)
            .Select(p => (int?)p.UserId).FirstOrDefaultAsync();
        return userId is int id && await userAccess.IsAdminAsync(id);
    }

    public async Task<bool> SetCurrentUserDmOptInAsync(bool optIn)
    {
        var userId = await userAccess.GetCurrentUserIdAsync();
        if (userId is null) return false;
        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId.Value);
        if (profile is null || string.IsNullOrEmpty(profile.DiscordUserId)) return false;
        profile.DiscordDmOptIn = optIn;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> UnlinkCurrentUserAsync()
    {
        var userId = await userAccess.GetCurrentUserIdAsync();
        if (userId is null) return false;
        var profile = await db.UserProfiles.FirstOrDefaultAsync(x => x.UserId == userId.Value);
        if (profile is null) return false;
        profile.DiscordUserId = null;
        profile.DiscordUsername = null;
        profile.DiscordDmOptIn = false;
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

    internal static bool IsValidDiscordUserId(string value) =>
        value.Length is >= 17 and <= 20 && value.All(char.IsAsciiDigit);

    private static bool IsValidCode(string value) =>
        value.Length == 6 && value.All(Alphabet.Contains);

    private sealed class LinkTicket(int userId)
    {
        private int _consumed;

        public bool TryConsume(out int value)
        {
            value = userId;
            return Interlocked.Exchange(ref _consumed, 1) == 0;
        }
    }
}
