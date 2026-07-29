using Microsoft.Extensions.DependencyInjection;
using PlexRequests.Downloader.Api;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Configuration;

/// <summary>
/// Per-indexer enable/priority state from the web app's admin Indexers panel, refreshed on the same
/// throttled-fetch pattern as <see cref="DownloadPreferencesProvider"/>. Providers the panel doesn't know
/// yet (or when the web app is unreachable) default to enabled at normal priority, so a config outage
/// never silently disables searching.
/// </summary>
public interface IIndexerSettingsProvider
{
    bool IsEnabled(string providerName);
    /// <summary>1 (highest) – 50 (lowest); 25 when unknown. Accepts a candidate Source value, which for
    /// Torznab is "Torznab:{endpoint}" — resolved by the prefix before the colon.</summary>
    int PriorityOf(string sourceName);
    Task RefreshAsync(CancellationToken ct);
}

public class IndexerSettingsProvider(IServiceProvider services, ILogger<IndexerSettingsProvider> logger)
    : IIndexerSettingsProvider
{
    public const int DefaultPriority = 25;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile Dictionary<string, (bool Enabled, int Priority)> _byName = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastFetch = DateTime.MinValue;

    public bool IsEnabled(string providerName) =>
        !_byName.TryGetValue(Normalize(providerName), out var s) || s.Enabled;

    public int PriorityOf(string sourceName) =>
        _byName.TryGetValue(Normalize(sourceName), out var s) ? s.Priority : DefaultPriority;

    // "Torznab:jackett" → "Torznab": endpoints roll up under the one Torznab row.
    private static string Normalize(string name)
    {
        var idx = name.IndexOf(':');
        return idx > 0 ? name[..idx] : name;
    }

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
                _byName = list
                    .Where(i => !string.IsNullOrWhiteSpace(i.Name))
                    .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => (g.First().Enabled, g.First().Priority), StringComparer.OrdinalIgnoreCase);
                var disabled = _byName.Where(kv => !kv.Value.Enabled).Select(kv => kv.Key).ToList();
                if (disabled.Count > 0)
                    logger.LogInformation("Indexer settings refreshed; disabled: {Disabled}", string.Join(", ", disabled));
            }
            _lastFetch = DateTime.UtcNow; // also on null: keep last-good, retry next interval
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not refresh indexer settings; using last-known values");
        }
        finally { _lock.Release(); }
    }
}
