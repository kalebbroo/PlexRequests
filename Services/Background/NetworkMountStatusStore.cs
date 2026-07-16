using System.Collections.Concurrent;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Background;

/// <summary>
/// Singleton, in-memory record of the web container's last mount attempt per share slug. Written by
/// <see cref="WebNetworkMountService"/> and read by <see cref="Implementations.NetworkShareService"/>
/// so the admin UI can show whether each share actually mounted (and why not). Not persisted — it's
/// live runtime state that's rebuilt on every reconcile.
/// </summary>
public interface INetworkMountStatusStore
{
    NetworkMountStatusDto? Get(string slug);
    void Set(string slug, bool mounted, string? error);
    /// <summary>Drop status for slugs that no longer exist, so removed shares don't linger in the UI.</summary>
    void Prune(IEnumerable<string> liveSlugs);
}

public class NetworkMountStatusStore : INetworkMountStatusStore
{
    private readonly ConcurrentDictionary<string, NetworkMountStatusDto> _map = new();

    public NetworkMountStatusDto? Get(string slug) => _map.TryGetValue(slug, out var s) ? s : null;

    public void Set(string slug, bool mounted, string? error) =>
        _map[slug] = new NetworkMountStatusDto { Mounted = mounted, Error = error, CheckedAt = DateTime.UtcNow };

    public void Prune(IEnumerable<string> liveSlugs)
    {
        var keep = new HashSet<string>(liveSlugs, StringComparer.Ordinal);
        foreach (var key in _map.Keys)
            if (!keep.Contains(key)) _map.TryRemove(key, out _);
    }
}
