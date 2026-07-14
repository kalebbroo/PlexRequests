using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Api;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Configuration;

/// <summary>
/// Effective download-selection preferences the ranker/pipeline read. Sourced from the web app's
/// admin config (<see cref="DownloadPreferencesDto"/>) with the local <see cref="QualityOptions"/>
/// appsettings as the offline fallback, so the downloader still runs standalone.
/// </summary>
public class EffectiveDownloadPreferences
{
    public SeasonPackStrategy SeasonPackStrategy { get; init; } = SeasonPackStrategy.PreferPack;
    public bool AllowEpisodeFallback { get; init; } = true;
    public int MaxEpisodesForFanout { get; init; } = 30;
    public int MinSeeders { get; init; } = 1;
    public double MaxSizeGb { get; init; } = 25;
    public double MaxSeasonPackSizeGb { get; init; } = 80;
    public string[] PreferredGroups { get; init; } = Array.Empty<string>();
    public bool PreferX265 { get; init; } = true;
    public bool PreferHdr { get; init; }
    public bool PreferHigherQualitySource { get; init; } = true;
    public bool EnforceQualityFloor { get; init; } = true;
    public double MinTitleSimilarity { get; init; } = 0.5;
}

public interface IDownloadPreferencesProvider
{
    /// <summary>The last successfully fetched preferences, or the appsettings fallback until the first fetch.</summary>
    EffectiveDownloadPreferences Current { get; }
    /// <summary>Fetch the latest admin config, throttled to at most once per refresh interval.</summary>
    Task RefreshAsync(CancellationToken ct);
}

public class DownloadPreferencesProvider : IDownloadPreferencesProvider
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);

    private readonly IServiceProvider _services;
    private readonly ILogger<DownloadPreferencesProvider> _logger;
    private readonly EffectiveDownloadPreferences _fallback;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile EffectiveDownloadPreferences _current;
    private DateTime _lastFetch = DateTime.MinValue;

    public DownloadPreferencesProvider(
        IServiceProvider services,
        IOptions<QualityOptions> quality,
        ILogger<DownloadPreferencesProvider> logger)
    {
        _services = services;
        _logger = logger;
        var q = quality.Value;
        // Seed defaults from the local appsettings thresholds; these apply until (and if) the web config loads.
        _fallback = new EffectiveDownloadPreferences
        {
            MinSeeders = q.MinSeeders,
            MaxSizeGb = q.MaxSizeGb,
            PreferredGroups = q.PreferredGroups ?? Array.Empty<string>()
        };
        _current = _fallback;
    }

    public EffectiveDownloadPreferences Current => _current;

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastFetch < RefreshInterval) return;
        if (!await _lock.WaitAsync(0, ct)) return; // another refresh in flight; skip
        try
        {
            if (DateTime.UtcNow - _lastFetch < RefreshInterval) return;
            // IPlexRequestsApiClient is a typed HttpClient (transient) — resolve per call rather than
            // capturing it in this singleton, so handler rotation is preserved.
            using var scope = _services.CreateScope();
            var api = scope.ServiceProvider.GetRequiredService<IPlexRequestsApiClient>();
            var dto = await api.GetConfigAsync(ct);
            if (dto is not null)
            {
                _current = Map(dto);
                _lastFetch = DateTime.UtcNow;
                _logger.LogInformation("Download preferences refreshed (strategy={Strategy}, minSeeders={Min})",
                    _current.SeasonPackStrategy, _current.MinSeeders);
            }
            else
            {
                // Keep last-good/fallback; retry next interval.
                _lastFetch = DateTime.UtcNow;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not refresh download preferences; using last-known values");
        }
        finally { _lock.Release(); }
    }

    private static EffectiveDownloadPreferences Map(DownloadPreferencesDto d) => new()
    {
        SeasonPackStrategy = d.SeasonPackStrategy,
        AllowEpisodeFallback = d.AllowEpisodeFallback,
        MaxEpisodesForFanout = d.MaxEpisodesForFanout,
        MinSeeders = d.MinSeeders,
        MaxSizeGb = d.MaxSizeGb,
        MaxSeasonPackSizeGb = d.MaxSeasonPackSizeGb,
        PreferredGroups = string.IsNullOrWhiteSpace(d.PreferredGroupsCsv)
            ? Array.Empty<string>()
            : d.PreferredGroupsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        PreferX265 = d.PreferX265,
        PreferHdr = d.PreferHdr,
        PreferHigherQualitySource = d.PreferHigherQualitySource,
        EnforceQualityFloor = d.EnforceQualityFloor,
        MinTitleSimilarity = d.MinTitleSimilarity
    };
}
