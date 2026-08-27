using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

public sealed class NotificationPreferenceService(
    AppDbContext db,
    IUserAccessService userAccess) : INotificationPreferenceService
{
    public event Action<NotificationPreferencesDto>? Changed;

    internal static readonly long SupportedMask = Enum.GetValues<NotificationType>()
        .Aggregate(0L, (mask, type) => mask | NotificationPreferencesDto.Bit(type));

    public async Task<NotificationPreferencesDto> GetCurrentAsync()
    {
        var userId = await userAccess.GetCurrentUserIdAsync();
        if (userId is null) return new NotificationPreferencesDto();

        return await db.UserProfiles.AsNoTracking()
            .Where(x => x.UserId == userId.Value)
            .Select(x => new NotificationPreferencesDto
            {
                WebTypeMask = x.WebNotificationTypes,
                // A legacy true value represented an explicit all-or-nothing choice. Migrations reset the
                // historical auto-opt-in rows, while this fallback keeps manually-created integrations and
                // rolling-version tests compatible until the first per-event save.
                DiscordTypeMask = x.DiscordNotificationTypes == 0 && x.DiscordDmOptIn
                    ? SupportedMask
                    : x.DiscordNotificationTypes,
                ReadBehavior = x.NotificationReadBehavior,
                AutoReadSeconds = x.NotificationAutoReadSeconds
            })
            .FirstOrDefaultAsync() ?? new NotificationPreferencesDto();
    }

    public async Task<bool> UpdateCurrentAsync(NotificationPreferencesDto preferences)
    {
        var userId = await userAccess.GetCurrentUserIdAsync();
        if (userId is null) return false;
        var profile = await db.UserProfiles.FirstOrDefaultAsync(x => x.UserId == userId.Value);
        if (profile is null) return false;

        profile.WebNotificationTypes = preferences.WebTypeMask & SupportedMask;
        profile.DiscordNotificationTypes = preferences.DiscordTypeMask & SupportedMask;
        profile.DiscordDmOptIn = profile.DiscordNotificationTypes != 0;
        profile.NotificationReadBehavior = Enum.IsDefined(preferences.ReadBehavior)
            ? preferences.ReadBehavior
            : NotificationReadBehavior.Manual;
        profile.NotificationAutoReadSeconds = Math.Clamp(preferences.AutoReadSeconds, 10, 3600);
        await db.SaveChangesAsync();
        preferences.WebTypeMask = profile.WebNotificationTypes;
        preferences.DiscordTypeMask = profile.DiscordNotificationTypes;
        preferences.ReadBehavior = profile.NotificationReadBehavior;
        preferences.AutoReadSeconds = profile.NotificationAutoReadSeconds;
        Changed?.Invoke(new NotificationPreferencesDto
        {
            WebTypeMask = profile.WebNotificationTypes,
            DiscordTypeMask = profile.DiscordNotificationTypes,
            ReadBehavior = profile.NotificationReadBehavior,
            AutoReadSeconds = profile.NotificationAutoReadSeconds
        });
        return true;
    }
}
