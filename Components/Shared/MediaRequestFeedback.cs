using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Components.Shared;

/// <summary>Keeps request confirmations consistent across every card surface.</summary>
public static class MediaRequestFeedback
{
    public static string Success(MediaCardDto media, MediaRequestResult result)
    {
        if (result.RequestScope != RequestScopeKind.Series)
            return $"{media.Title} has been requested!";

        if (!result.MonitorsFutureReleases)
            return $"{media.Title}: full series requested.";

        return result.NewStatus == RequestStatus.Pending
            ? $"{media.Title}: full series requested. Once approved, future episodes will be monitored automatically."
            : $"{media.Title}: full series requested. Future episodes will be monitored automatically.";
    }
}
