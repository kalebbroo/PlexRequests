using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

public class NotificationBroker : INotificationBroker
{
    public event Action<NotificationDto>? Notified;

    public void Publish(NotificationDto notification) => Notified?.Invoke(notification);
}
