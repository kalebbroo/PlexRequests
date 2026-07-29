using System.ComponentModel.DataAnnotations;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// Admin-controlled state + rolling health for one indexer provider in the downloader (the app's own
/// Jackett-like indexer layer). A row is auto-created the first time a provider reports a search, and
/// pre-seeded for the built-ins, so the admin "Indexers" panel always has something to show. Enabled and
/// Priority are served to the downloader over /api/fulfillment/indexers; the health columns are written
/// by the downloader's per-search status reports and are display-only.
/// </summary>
public class IndexerSettingEntity
{
    public int Id { get; set; }

    /// <summary>Provider name as reported by the downloader (e.g. "EZTV", "1337x", "PirateBay"). Unique.</summary>
    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>1 (highest) – 50 (lowest); default 25. Feeds a small ranking bonus so equally-good releases
    /// prefer the more trusted indexer — it never overrides quality/seeder differences.</summary>
    public int Priority { get; set; } = 25;

    // --- Rolling health, updated from the downloader's per-search reports --------------------------
    public DateTime? LastSearchAt { get; set; }
    public int LastResultCount { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    [MaxLength(512)] public string? LastError { get; set; }
    /// <summary>Consecutive failed searches; reset to 0 on any success. High values = the site is down/blocked.</summary>
    public int ConsecutiveFailures { get; set; }
    public long TotalSearches { get; set; }
    public long TotalResults { get; set; }
    public int LastLatencyMs { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
