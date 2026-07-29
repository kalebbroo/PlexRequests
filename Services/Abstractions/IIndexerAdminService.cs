using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Abstractions;

/// <summary>
/// Backs the admin "Indexers" panel and the downloader's indexer-config/telemetry endpoints — the control
/// plane for the app's own Jackett-like indexer layer. Enabled/Priority flow admin → downloader; the
/// health columns flow downloader → admin via per-search status reports.
/// </summary>
public interface IIndexerAdminService
{
    /// <summary>All known indexers with health, ordered by priority then name (for the admin panel).</summary>
    Task<List<IndexerSettingDto>> GetAllAsync();

    Task<bool> SetEnabledAsync(string name, bool enabled);

    /// <summary>Set 1 (highest) – 50 (lowest); values are clamped.</summary>
    Task<bool> SetPriorityAsync(string name, int priority);

    /// <summary>Slim enabled/priority list the downloader polls (unknown providers default to enabled).</summary>
    Task<List<IndexerConfigDto>> GetWorkerConfigAsync();

    /// <summary>Apply a batch of per-search outcomes from the downloader; unknown provider names are
    /// auto-registered so new indexers appear in the panel without a schema/seed change.</summary>
    Task ReportStatusAsync(List<IndexerStatusReportDto> reports);
}
