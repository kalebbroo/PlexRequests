namespace PlexRequestsHosted.Infrastructure.Catalog;

/// <summary>
/// Settings for the disposable release catalog. The catalog is intentionally opt-in until ingestion and
/// matching have completed a shadow-mode rollout; the existing live-search path remains available when it
/// is disabled.
/// </summary>
public sealed class CatalogOptions
{
    public const string Section = "Catalog";

    public bool Enabled { get; set; }
    public bool UseForSearch { get; set; }
    public bool UseForMonitoring { get; set; }
    public int RetentionDays { get; set; } = 90;
    public int UnresolvedRetentionDays { get; set; } = 14;
    public int MaxBatchSize { get; set; } = 250;
    public int MaxDatabaseSizeMb { get; set; } = 2048;
}
