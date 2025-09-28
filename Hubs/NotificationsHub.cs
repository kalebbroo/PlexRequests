using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace PlexRequestsHosted.Hubs;

[Authorize]
public class NotificationsHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            var username = user.Identity!.Name;
            if (!string.IsNullOrWhiteSpace(username))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{username}");
            }
            if (user.IsInRole("Admin"))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, "admins");
            }
        }
        await base.OnConnectedAsync();
    }
}
