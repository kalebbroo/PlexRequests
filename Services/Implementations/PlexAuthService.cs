using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Utils;

namespace PlexRequestsHosted.Services.Implementations;

public class PlexAuthService : IPlexAuthService
{
    private readonly HttpClient _httpClient;
    private readonly IJSRuntime _jsRuntime;
    private readonly PlexConfiguration _config;

    private const string BaseApiUrl = "https://plex.tv/api/v2";

    public PlexAuthService(HttpClient httpClient, IJSRuntime jsRuntime, IOptions<PlexConfiguration> options)
    {
        _httpClient = httpClient;
        _jsRuntime = jsRuntime;
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

            string authUrl = $"https://app.plex.tv/auth#?clientID={Uri.EscapeDataString(_config.ClientIdentifier)}&code={Uri.EscapeDataString(pin.Code)}&context[device][product]={Uri.EscapeDataString(_config.Product)}";

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
            Logs.Info($"Polling Plex PIN {pinId}");
            var pollStart = DateTime.UtcNow;
            var pollTimeout = TimeSpan.FromSeconds(_config.PinPollingTimeoutSeconds);
            var pollInterval = TimeSpan.FromSeconds(_config.PinPollingIntervalSeconds);

            while (!cancellationToken.IsCancellationRequested)
            {
                if (DateTime.UtcNow - pollStart > pollTimeout)
                {
                    return new PlexAuthenticationResult { Success = false, ErrorMessage = "Authentication timeout - please try again" };
                }

                var url = $"{BaseApiUrl}/pins/{pinId}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                var res = await _httpClient.SendAsync(req, cancellationToken);
                if (!res.IsSuccessStatusCode)
                {
                    await Task.Delay(pollInterval, cancellationToken);
                    continue;
                }

                var json = await res.Content.ReadAsStringAsync(cancellationToken);
                var pin = JsonSerializer.Deserialize<PlexPinResponse>(json, JsonOpts);
                if (pin?.AuthToken is not null)
                {
                    // fetch user info for convenience
                    var user = await GetPlexUserAsync(pin.AuthToken);
                    return new PlexAuthenticationResult { Success = true, AuthToken = pin.AuthToken, User = user };
                }

                // expired?
                if (pin?.ExpiresAt != null && pin.ExpiresAt <= DateTime.UtcNow)
                {
                    return new PlexAuthenticationResult { Success = false, ErrorMessage = "Authentication PIN has expired" };
                }

                await Task.Delay(pollInterval, cancellationToken);
            }

            return new PlexAuthenticationResult { Success = false, ErrorMessage = "Authentication was cancelled" };
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

    public async Task<bool> OpenAuthenticationWindowAsync(string authUrl)
    {
        try
        {
            await _jsRuntime.InvokeVoidAsync("open", authUrl, "_blank");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Warning($"Failed to open auth window via JS: {ex.Message}");
            return false;
        }
    }

    public async Task<PlexUser?> GetPlexUserAsync(string authToken)
    {
        if (string.IsNullOrWhiteSpace(authToken)) return null;

        try
        {
            var url = $"{BaseApiUrl}/user";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            EnsureDefaultHeaders(req.Headers);
            req.Headers.Add("X-Plex-Token", authToken);

            var res = await _httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                Logs.Warning($"GetPlexUser failed: {(int)res.StatusCode} {res.ReasonPhrase}");
                return null;
            }

            var json = await res.Content.ReadAsStringAsync();
            var account = JsonSerializer.Deserialize<PlexUserAccount>(json, JsonOpts);
            if (account is null) return null;

            return new PlexUser
            {
                Id = account.Uuid ?? string.Empty,
                Username = account.Username ?? string.Empty,
                Email = account.Email ?? string.Empty,
                Thumb = account.Thumb ?? string.Empty,
                Title = account.Title ?? string.Empty,
                HasPassword = account.HasPassword
            };
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
