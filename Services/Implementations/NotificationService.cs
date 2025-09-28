using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using PlexRequestsHosted.Hubs;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

public class NotificationService(IHubContext<NotificationsHub> hub) : INotificationService
{
    private readonly IHubContext<NotificationsHub> _hub = hub;

    public Task RequestCreatedAsync(MediaRequestDto request)
    {
        // Admins should be notified of new requests
        return _hub.Clients.Group("admins").SendAsync("RequestCreated", request);
        // TODO: Discord channel webhook announce (title, poster, requester, when, status)
        // TODO: If requestor opted into Discord DM, send DM via bot
    }

    public Task RequestApprovedAsync(MediaRequestDto request)
    {
        // Notify the requester and admins
        var userGroup = $"user:{request.RequestedByUsername}";
        return _hub.Clients.Groups(userGroup, "admins").SendAsync("RequestApproved", request);
        // TODO: Discord channel webhook announce
        // TODO: Discord DM if user opted in
    }

    public Task RequestRejectedAsync(MediaRequestDto request)
    {
        var userGroup = $"user:{request.RequestedByUsername}";
        return _hub.Clients.Groups(userGroup, "admins").SendAsync("RequestRejected", request);
        // TODO: Discord channel webhook announce
        // TODO: Discord DM if user opted in
    }

    public Task RequestAvailableAsync(MediaRequestDto request)
    {
        var userGroup = $"user:{request.RequestedByUsername}";
        return _hub.Clients.Groups(userGroup, "admins").SendAsync("RequestAvailable", request);
        // TODO: Discord channel webhook announce
        // TODO: Discord DM if user opted in
    }
}
