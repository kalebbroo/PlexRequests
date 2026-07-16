using System.Net;
using System.Net.Sockets;

namespace PlexRequestsHosted.Shared;

/// <summary>Shared server-address heuristics for network shares (used by both the service and the UI).</summary>
public static class NetworkShareServerHelper
{
    /// <summary>
    /// True unless <paramref name="server"/> is a clearly public IP literal. Hostnames return true
    /// (unknown — don't nag), so the "not a LAN address" warning only fires on the real footgun: a
    /// public IP, which would send SMB/NFS traffic and credentials over the internet.
    /// </summary>
    public static bool LooksPrivate(string? server)
    {
        if (!IPAddress.TryParse(server?.Trim(), out var ip)) return true; // hostname — unknown
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254); // link-local
        }
        // IPv6: ULA fc00::/7 or link-local fe80::/10
        var v6 = ip.GetAddressBytes();
        return (v6[0] & 0xFE) == 0xFC || (v6[0] == 0xFE && (v6[1] & 0xC0) == 0x80);
    }
}
