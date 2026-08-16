using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// One searchable indexer. Covers both the built-in public scrapers/APIs and admin-added Torznab endpoints
/// (Jackett/Prowlarr) — <see cref="Implementation"/> is the discriminator and a built-in is simply a row with
/// no <see cref="Url"/>.
///
/// One table rather than two because everything except the URL and API key is shared: enable/priority,
/// per-media-type support, categories, rate limits, caching, and the rolling health the panel displays.
/// Splitting them would duplicate all of that and force the admin panel to union two shapes for no gain.
///
/// This replaces the old settings-only row, which had no URL/key/category columns at all. Consequently every
/// Torznab endpoint configured in the downloader's environment collapsed onto a single "Torznab" row, so they
/// shared one enable toggle, one priority and one health record no matter how many were configured.
/// </summary>
public class IndexerEntity
{
    public int Id { get; set; }

    /// <summary>Display name, unique. For built-ins this matches the implementation key ("EZTV", "1337x");
    /// for Torznab endpoints the admin names them ("Prowlarr/TorrentLeech").</summary>
    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Which code path searches this row: an implementation key such as "Torznab", "EZTV", "1337x".
    /// The downloader resolves its <c>IIndexerImplementation</c> by this, not by <see cref="Name"/>.</summary>
    [MaxLength(32)]
    public string Implementation { get; set; } = string.Empty;

    /// <summary>Seeded by the app and not deletable — only disableable — so a row can't vanish and silently
    /// reduce coverage.</summary>
    public bool IsBuiltIn { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>1 (highest) – 50 (lowest); default 25. Feeds a small ranking bonus so equally-good releases
    /// prefer the more trusted indexer — it never overrides quality/seeder differences.</summary>
    public int Priority { get; set; } = 25;

    // --- Endpoint configuration (Torznab/Newznab; null for built-ins) ------------------------------
    /// <summary>Full Torznab API URL, e.g. https://prowlarr.local/1/api.</summary>
    [MaxLength(2048)] public string? Url { get; set; }

    /// <summary>API key, encrypted at rest with IDataProtection — same treatment as network-share passwords.
    /// Decrypted only when handed to the downloader over the authenticated worker API.</summary>
    [MaxLength(1024)] public string? ApiKeyEncrypted { get; set; }

    /// <summary>Optional override of the built-in's base URL, for mirrors.</summary>
    [MaxLength(2048)] public string? BaseUrlOverride { get; set; }

    /// <summary>
    /// A Cloudflare clearance cookie harvested from a real browser, stored as a raw Cookie header
    /// ("cf_clearance=…") and encrypted at rest like the API key. Paired with <see cref="UserAgent"/>
    /// because Cloudflare binds the cookie to the exact User-Agent AND the client IP that earned it —
    /// replaying it under a different UA is worse than sending nothing, since it reads as a stolen cookie.
    /// This is an optional manual override for deployments with stable egress; the application does not
    /// attempt to solve browser challenges automatically.
    /// </summary>
    [MaxLength(4096)] public string? ClearanceCookieEncrypted { get; set; }
    [MaxLength(512)] public string? UserAgent { get; set; }
    /// <summary>When the clearance was obtained, so the UI can say how stale it is (they expire).</summary>
    public DateTime? ClearanceObtainedAt { get; set; }

    // --- Search scope -------------------------------------------------------------------------------
    /// <summary>Torznab category ids per media type. Previously hardcoded (2000 movies / 5000 TV / 5070 anime),
    /// which is wrong for trackers that use their own numbering.</summary>
    [MaxLength(256)] public string? MovieCategoriesCsv { get; set; }
    [MaxLength(256)] public string? TvCategoriesCsv { get; set; }
    [MaxLength(256)] public string? AnimeCategoriesCsv { get; set; }

    public bool SupportsMovie { get; set; } = true;
    public bool SupportsTv { get; set; } = true;
    public bool SupportsAnime { get; set; } = true;

    /// <summary>Only searched for jobs classified as anime (Nyaa), so an ordinary title can never match an
    /// anime release with a coincidentally overlapping name.</summary>
    public bool AnimeOnly { get; set; }

    // --- Independent toggles, mirroring what the search paths actually need ------------------------
    /// <summary>Include in incremental catalog ingestion and periodic monitoring.</summary>
    public bool EnableIngestion { get; set; } = true;
    /// <summary>Include when the downloader searches for a queued request.</summary>
    public bool EnableAutomaticSearch { get; set; } = true;
    /// <summary>Include when an admin runs a manual search.</summary>
    public bool EnableInteractiveSearch { get; set; } = true;

    /// <summary>
    /// Use this indexer's newest-uploads listing to populate the home page's "Recommended" row. Only
    /// implementations that can browse (rather than only search) can serve this, so it's off by default and
    /// enabled per row — which also makes the source swappable rather than hardcoded to one site.
    /// </summary>
    public bool EnableRecommendedFeed { get; set; }

    // --- Per-indexer limits -------------------------------------------------------------------------
    public int? MinSeeders { get; set; }
    public double? SeedRatio { get; set; }
    public int? SeedTimeMinutes { get; set; }
    public int? SeasonPackSeedTimeMinutes { get; set; }

    public int? TimeoutSeconds { get; set; }

    /// <summary>Requests per minute allowed against this indexer. Guards public sites (and our own IP)
    /// against the previously unbounded fan-out.</summary>
    public int RateLimitPerMinute { get; set; } = 30;

    /// <summary>How long an identical query's results are reused. Matters most for repeated manual searches
    /// and legacy live monitoring, which would otherwise re-hit every site each pass.</summary>
    public int CacheSeconds { get; set; } = 300;

    /// <summary>For scrapers that must open a detail page per result to find a magnet: how many to open.
    /// The default used to be 8, chosen strictly by seeder count and before any quality filtering, which
    /// routinely discarded every 1080p release on the page.</summary>
    public int MaxDetailFetches { get; set; } = 25;

    /// <summary>Ignore results older than this many days. Null = no age limit.</summary>
    public int? MaxAgeDays { get; set; }

    [MaxLength(512)] public string? Notes { get; set; }

    // --- Rolling health, written by the downloader's per-search reports ----------------------------
    public DateTime? LastSearchAt { get; set; }
    public int LastResultCount { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    [MaxLength(512)] public string? LastError { get; set; }
    /// <summary>Consecutive failed searches; reset to 0 on any success. High values = the site is down/blocked.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// Why the last search was refused, when it was. Kept separate from <see cref="LastError"/> because a
    /// block is a distinct condition with a distinct remedy: 1337x and ext.to each ran 13 searches that
    /// returned nothing and still showed a clean health record, because the provider swallowed the 403 and
    /// reported "success, 0 results". Blocked must never look like quiet.
    /// </summary>
    public IndexerBlockReason LastBlockReason { get; set; }
    public DateTime? LastBlockedAt { get; set; }
    /// <summary>Durable live-search circuit. The worker skips this source until the probe time.</summary>
    public DateTime? SearchCircuitOpenUntil { get; set; }
    public long TotalSearches { get; set; }
    public long TotalResults { get; set; }
    public int LastLatencyMs { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether this row can serve a job of the given media type at all.</summary>
    public bool Supports(MediaType mediaType) => mediaType switch
    {
        MediaType.Movie => SupportsMovie,
        MediaType.TvShow => SupportsTv,
        MediaType.Anime => SupportsAnime,
        _ => false
    };
}
