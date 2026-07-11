using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Abstractions;

/// <summary>
/// In-process publish/subscribe used to push live notifications to Blazor Server
/// components without a SignalR client round-trip (which cannot carry the auth cookie
/// from a server-originated connection). Registered as a singleton; components subscribe
/// to <see cref="Notified"/> and filter by <see cref="NotificationDto.UserId"/>.
/// </summary>
public interface INotificationBroker
{
    event Action<NotificationDto>? Notified;
    void Publish(NotificationDto notification);
}
