using Microsoft.Extensions.DependencyInjection;
using PlexRequests.Downloader.Api;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Configuration;

/// <summary>
/// The downloader's view of the admin-configured indexers, refreshed on the same throttled-fetch pattern as
/// <see cref="DownloadPreferencesProvider"/>. Each row is a full definition (URL, key, categories, limits),
/// not just an enable flag — which is what lets several Torznab endpoints exist independently.
///
/// The previous version keyed everything on a provider NAME and normalised "Torznab:jackett" down to
/// "Torznab", so every configured endpoint shared one enable toggle, one priority and one health record.
/// </summary>
public interface IIndexerSettingsProvider
{
    /// <summary>Every configured indexer, enabled or not. Empty until the first successful fetch.</summary>
    IReadOnlyList<IndexerConfigDto> All { get; }

    /// <summary>Priority for a specific indexer row: 1 (highest) – 50 (lowest); 25 when unknown.</summary>
    int PriorityOf(int indexerId);

    Task RefreshAsync(CancellationToken ct);
}

public class IndexerSettingsProvider(IServiceProvider services, ILogger<IndexerSettingsProvider> logger)
    : IIndexerSettingsProvider
{
    public const int DefaultPriority = 25;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile IReadOnlyList<IndexerConfigDto> _all = Array.Empty<IndexerConfigDto>();
    private DateTime _lastFetch = DateTime.MinValue;

    public IReadOnlyList<IndexerConfigDto> All => _all;

    public int PriorityOf(int indexerId) =>
        _all.FirstOrDefault(i => i.Id == indexerId)?.Priority ?? DefaultPriority;

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastFetch < RefreshInterval) return;
        if (!await _lock.WaitAsync(0, ct)) return; // another refresh in flight; skip
        try
        {
            if (DateTime.UtcNow - _lastFetch < RefreshInterval) return;
            using var scope = services.CreateScope();
            var api = scope.ServiceProvider.GetRequiredService<IPlexRequestsApiClient>();
            var list = await api.GetIndexersAsync(ct);
            if (list is not null)
            {
                _all = list;
                _lastFetch = DateTime.UtcNow;
                logger.LogInformation("Indexer definitions refreshed ({Enabled} of {Total} enabled)",
                    list.Count(i => i.Enabled), list.Count);
            }
            else
            {
                // Keep last-good and retry next interval rather than hammering a web app that's down.
                _lastFetch = DateTime.UtcNow;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not refresh indexer definitions; using last-known values");
        }
        finally { _lock.Release(); }
    }
}
