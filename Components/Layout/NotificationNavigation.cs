using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Components.Layout;

internal static class NotificationNavigation
{
    internal static string Destination(NotificationType type, int? relatedRequestId = null) => type switch
    {
        NotificationType.RequestCreated => "/admin?tab=requests&sub=approvals",
        NotificationType.MediaIssueReported => "/admin?tab=requests&sub=issues",
        NotificationType.RequestSearchStalled when relatedRequestId is int requestId and > 0
            => $"/admin?tab=jobs&reviewRequest={requestId}",
        NotificationType.RequestSearchStalled or NotificationType.Error => "/admin?tab=jobs",
        NotificationType.StorageWarning => "/admin?tab=overview",
        _ => "/requests"
    };
}
