using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Components.Layout;

internal static class NotificationNavigation
{
    internal static string Destination(NotificationType type) => type switch
    {
        NotificationType.RequestCreated => "/admin?tab=requests&sub=approvals",
        NotificationType.MediaIssueReported => "/admin?tab=requests&sub=issues",
        NotificationType.RequestSearchStalled or NotificationType.Error => "/admin?tab=jobs",
        _ => "/requests"
    };
}
