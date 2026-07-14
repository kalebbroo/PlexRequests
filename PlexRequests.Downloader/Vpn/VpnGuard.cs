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

            // Weak but real signal on top of "the HTTP call succeeded": if egress resolves to this
            // host's own known public IP, the VPN tunnel likely isn't actually in the traffic path even
            // though outbound connectivity works fine. Note this only covers THIS process's own HTTP
            // calls (indexer scraping, Deluge JSON-RPC) — it has no visibility into Deluge's own torrent
            // traffic, which is the traffic that actually needs VPN protection; that's an infra concern
            // (Deluge's own outgoing_interface bind), not something this check can verify.
            if (!string.IsNullOrWhiteSpace(_opts.ExpectedNonVpnIp))
            {
                var egressIp = (await resp.Content.ReadAsStringAsync(cts.Token)).Trim();
                if (string.Equals(egressIp, _opts.ExpectedNonVpnIp, StringComparison.Ordinal))
                {
                    _logger.LogWarning("VPN health check egress IP ({Ip}) matches the configured non-VPN IP; holding off — tunnel likely down", egressIp);
                    return false;
                }
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
