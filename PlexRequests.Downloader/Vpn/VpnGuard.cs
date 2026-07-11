using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;

namespace PlexRequests.Downloader.Vpn;

public interface IVpnGuard
{
    /// <summary>True if it's safe to do network work. When routed through the VPN sidecar, a downed
    /// VPN means no egress at all, so this outbound probe fails and the worker holds off.</summary>
    Task<bool> IsHealthyAsync(CancellationToken ct);
}

public class VpnGuard(HttpClient http, IOptions<VpnOptions> options, ILogger<VpnGuard> logger) : IVpnGuard
{
    private readonly HttpClient _http = http;
    private readonly VpnOptions _opts = options.Value;
    private readonly ILogger<VpnGuard> _logger = logger;

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        if (!_opts.Enabled) return true;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, _opts.TimeoutSeconds)));
            using var resp = await _http.GetAsync(_opts.HealthCheckUrl, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("VPN health check returned {Status}; holding off", (int)resp.StatusCode);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("VPN health check failed ({Message}); holding off — is the VPN up?", ex.Message);
            return false;
        }
    }
}
