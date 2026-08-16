using System.Net.WebSockets;

namespace PlexRequests.Downloader.Indexers;

/// <summary>The browser-only transport used by 1337x. Other providers remain on HttpClient.</summary>
public interface IInteractiveBrowserTransport
{
    Task<string> GetStringAsync(string url, CancellationToken cancellationToken);
    Task RunInteractiveSessionAsync(WebSocket adminSocket, CancellationToken cancellationToken);
}
