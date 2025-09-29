using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Utils;

namespace PlexRequestsHosted.Services.Implementations;

public class PlexAuthService : IPlexAuthService
{
    private readonly HttpClient _httpClient;
    private readonly IJSRuntime _jsRuntime;
    private readonly NavigationManager _navigation;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly PlexConfiguration _config;

    private const string BaseApiUrl = "https://plex.tv/api/v2";

    public PlexAuthService(HttpClient httpClient, IJSRuntime jsRuntime, NavigationManager navigation, IHttpContextAccessor httpContextAccessor, IOptions<PlexConfiguration> options)
    {
        _httpClient = httpClient;
        _jsRuntime = jsRuntime;
        _navigation = navigation;
        _httpContextAccessor = httpContextAccessor;
        _config = options.Value;

        // default headers shared across requests (can be overridden per-request)
        EnsureDefaultHeaders(_httpClient.DefaultRequestHeaders);
    }

    public async Task<PlexAuthenticationFlow> BeginAuthenticationAsync()
    {
        try
        {
            Logs.Info("Starting Plex PIN authentication flow");

            var url = $"{BaseApiUrl}/pins?strong=true&X-Plex-Product={Uri.EscapeDataString(_config.Product)}&X-Plex-Client-Identifier={Uri.EscapeDataString(_config.ClientIdentifier)}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            var res = await _httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync();
                Logs.Error($"Failed to create PIN: {(int)res.StatusCode} {res.ReasonPhrase} {Trim(err)}");
                return new PlexAuthenticationFlow { Success = false, ErrorMessage = "Failed to create authentication PIN" };
            }

            var json = await res.Content.ReadAsStringAsync();
            var pin = JsonSerializer.Deserialize<PlexPinResponse>(json, JsonOpts);
            if (pin is null)
            {
                return new PlexAuthenticationFlow { Success = false, ErrorMessage = "Invalid PIN response from Plex" };
            }

            // Do NOT write to server session from a Blazor circuit event. Instead, pass pinId via callback URL
            // and optionally mirror it to browser sessionStorage for diagnostics.
            try
            {
                await _jsRuntime.InvokeVoidAsync("sessionStorage.setItem", "plex_pin_id", pin.Id.ToString());
            }
            catch { /* non-fatal */ }

            // Create callback URL with pinId so the server can complete auth without relying on session state
            var baseUri = _navigation.BaseUri.TrimEnd('/');
            var callbackUrl = $"{baseUri}/auth/callback?pinId={Uri.EscapeDataString(pin.Id.ToString())}";
            
            // Build Plex OAuth URL with callback
            string authUrl = $"https://app.plex.tv/auth#?clientID={Uri.EscapeDataString(_config.ClientIdentifier)}&code={Uri.EscapeDataString(pin.Code)}&context[device][product]={Uri.EscapeDataString(_config.Product)}&forwardUrl={Uri.EscapeDataString(callbackUrl)}";

            Logs.Info($"Created PIN {pin.Id} with callback URL: {callbackUrl}");

            return new PlexAuthenticationFlow
            {
                Success = true,
                PinId = pin.Id,
                Code = pin.Code,
                AuthenticationUrl = authUrl,
                ClientIdentifier = _config.ClientIdentifier
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"BeginAuthenticationAsync error: {ex}");
            return new PlexAuthenticationFlow { Success = false, ErrorMessage = "Authentication initialization failed" };
        }
    }

    public async Task<PlexAuthenticationResult> PollForAuthenticationAsync(int pinId, CancellationToken cancellationToken = default)
    {
        try
        {
            Logs.Info($"Checking Plex PIN {pinId} status");
            
            var url = $"{BaseApiUrl}/pins/{pinId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var res = await _httpClient.SendAsync(req, cancellationToken);
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync(cancellationToken);
                Logs.Error($"Failed to check PIN status: {(int)res.StatusCode} {res.ReasonPhrase} {Trim(err)}");
                return new PlexAuthenticationResult { Success = false, ErrorMessage = "Failed to check authentication status" };
            }

            var json = await res.Content.ReadAsStringAsync(cancellationToken);
            var pin = JsonSerializer.Deserialize<PlexPinResponse>(json, JsonOpts);
            
            if (pin?.AuthToken is not null)
            {
                Logs.Info($"PIN {pinId} authenticated successfully");
                // fetch user info for convenience
                var user = await GetPlexUserAsync(pin.AuthToken);
                return new PlexAuthenticationResult { Success = true, AuthToken = pin.AuthToken, User = user };
            }

            // Check if expired
            if (pin?.ExpiresAt != null && pin.ExpiresAt <= DateTime.UtcNow)
            {
                Logs.Warning($"PIN {pinId} has expired");
                return new PlexAuthenticationResult { Success = false, ErrorMessage = "Authentication PIN has expired" };
            }

            // Not authenticated yet
            Logs.Info($"PIN {pinId} not yet authenticated");
            return new PlexAuthenticationResult { Success = false, ErrorMessage = "Authentication not completed yet" };
        }
        catch (OperationCanceledException)
        {
            return new PlexAuthenticationResult { Success = false, ErrorMessage = "Authentication was cancelled" };
        }
        catch (Exception ex)
        {
            Logs.Error($"PollForAuthenticationAsync error: {ex}");
            return new PlexAuthenticationResult { Success = false, ErrorMessage = "Authentication failed" };
        }
    }

    public Task<bool> OpenAuthenticationWindowAsync(string authUrl)
    {
        try
        {
            // Redirect the current window instead of opening a new tab
            _navigation.NavigateTo(authUrl, forceLoad: true);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Logs.Warning($"Failed to redirect to auth URL: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    public async Task<PlexUser?> GetPlexUserAsync(string authToken)
    {
        if (string.IsNullOrWhiteSpace(authToken)) 
        {
            Logs.Warning("GetPlexUserAsync called with empty token");
            return null;
        }

        try
        {
            Logs.Info("Fetching Plex user info");
            var url = $"{BaseApiUrl}/user";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            EnsureDefaultHeaders(req.Headers);
            req.Headers.Add("X-Plex-Token", authToken);

            var res = await _httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync();
                Logs.Warning($"GetPlexUser failed: {(int)res.StatusCode} {res.ReasonPhrase} {Trim(err)}");
                return null;
            }

            var json = await res.Content.ReadAsStringAsync();
            var account = JsonSerializer.Deserialize<PlexUserAccount>(json, JsonOpts);
            if (account is null) 
            {
                Logs.Warning("Failed to deserialize Plex user response");
                return null;
            }

            var user = new PlexUser
            {
                Id = account.Uuid ?? string.Empty,
                Username = account.Username ?? string.Empty,
                Email = account.Email ?? string.Empty,
                Thumb = account.Thumb ?? string.Empty,
                Title = account.Title ?? string.Empty,
                HasPassword = account.HasPassword
            };
            
            Logs.Info($"Successfully fetched Plex user: {user.Username} ({user.Title})");
            return user;
        }
        catch (Exception ex)
        {
            Logs.Error($"GetPlexUserAsync error: {ex}");
            return null;
        }
    }

    private void EnsureDefaultHeaders(HttpRequestHeaders headers)
    {
        if (!headers.Contains("X-Plex-Product"))
        {
            headers.Add("X-Plex-Product", _config.Product);
            headers.Add("X-Plex-Version", _config.Version);
            headers.Add("X-Plex-Client-Identifier", _config.ClientIdentifier);
            headers.Add("X-Plex-Device", _config.Device);
            headers.Add("X-Plex-Platform", _config.Platform);
            headers.Accept.Clear();
            headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<int?> GetStoredPinIdAsync()
    {
        try
        {
            var pinIdStr = await _jsRuntime.InvokeAsync<string?>("sessionStorage.getItem", "plex_pin_id");
            if (int.TryParse(pinIdStr, out var pinId))
            {
                return pinId;
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"Failed to get stored PIN ID: {ex.Message}");
        }
        return null;
    }

    public async Task ClearStoredPinIdAsync()
    {
        try
        {
            await _jsRuntime.InvokeVoidAsync("sessionStorage.removeItem", "plex_pin_id");
        }
        catch (Exception ex)
        {
            Logs.Warning($"Failed to clear stored PIN ID: {ex.Message}");
        }
    }

    private static string Trim(string? s) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length > 512 ? s[..512] + "..." : s);

    // minimal models for PIN and user endpoints
    private sealed class PlexPinResponse
    {
        public int Id { get; set; }
        public string Code { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
        public string? AuthToken { get; set; }
    }

    private sealed class PlexUserAccount
    {
        public string? Uuid { get; set; }
        public string? Username { get; set; }
        public string? Email { get; set; }
        public string? Thumb { get; set; }
        public string? Title { get; set; }
        public bool HasPassword { get; set; }
    }
}

public class PlexConfiguration
{
    public string ClientIdentifier { get; set; } = Guid.NewGuid().ToString();
    public string Product { get; set; } = "PlexRequests";
    public string Version { get; set; } = "1.0.0";
    public string Device { get; set; } = "Web";
    public string Platform { get; set; } = "Web";
    public int PinPollingIntervalSeconds { get; set; } = 2;
    public int PinPollingTimeoutSeconds { get; set; } = 120;
    // Server access
    public string? PrimaryServerUrl { get; set; }
    public string? ServerToken { get; set; }
}
