using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Utils;
using Microsoft.JSInterop;

namespace PlexRequestsHosted.Services.Implementations;

public class PlexAuthService(HttpClient httpClient, IJSRuntime jsRuntime) : IPlexAuthService
{
    private readonly HttpClient _httpClient = httpClient;
    public Task<PlexAuthenticationFlow> BeginAuthenticationAsync()
    {
        Logs.Info("Starting Plex auth flow");
        // In a real implementation we would call Plex PIN API here.
        return Task.FromResult(new PlexAuthenticationFlow
        {
            Success = true,
            PinId = Random.Shared.Next(1000, 9999),
            Code = Guid.NewGuid().ToString("N").Substring(0, 8),
            AuthenticationUrl = "https://app.plex.tv/auth#"
        });
    }

    public async Task<PlexAuthenticationResult> PollForAuthenticationAsync(int pinId, CancellationToken cancellationToken = default)
    {
        Logs.Info($"Polling for Plex auth with pin {pinId}");
        // Simulate waiting/polling for user to complete auth. After a short delay, return a success.
        try
        {
            await Task.Delay(1500, cancellationToken);
            return new PlexAuthenticationResult
            {
                Success = true,
                AuthToken = "demo-plex-token",
                User = new PlexUser { Username = "demo-user" }
            };
        }
        catch (OperationCanceledException)
        {
            return new PlexAuthenticationResult { Success = false, ErrorMessage = "Authentication cancelled" };
        }
    }

    public async Task<bool> OpenAuthenticationWindowAsync(string authUrl)
    {
        try
        {
            await jsRuntime.InvokeVoidAsync("open", authUrl, "_blank");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Warning($"Failed to open auth window via JS. Will fallback to navigation. {ex.Message}");
            return false;
        }
    }

    public Task<PlexUser?> GetPlexUserAsync(string authToken)
    {
        Logs.Info($"Retrieving Plex user with token length {authToken?.Length}");
        return Task.FromResult<PlexUser?>(new PlexUser { Username = "demo" });
    }
}
