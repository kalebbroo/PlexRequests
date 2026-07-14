namespace PlexRequests.Downloader.Configuration;

/// <summary>Connection to the PlexRequests web app's fulfillment API.</summary>
public class ApiOptions
{
    public const string Section = "Api";
    public string BaseUrl { get; set; } = "http://localhost:5000";
    public string Key { get; set; } = string.Empty; // must match the web app's Fulfillment:ApiKey
}

/// <summary>Main worker loop tuning.</summary>
public class WorkerOptions
{
    public const string Section = "Worker";
    public int PollIntervalSeconds { get; set; } = 15;
    public int MonitorIntervalSeconds { get; set; } = 15;
    public int MaxConcurrent { get; set; } = 2;
    public int ClaimBatchSize { get; set; } = 5;
    public string WorkerId { get; set; } = "downloader";
    /// <summary>Where in-flight job↔torrent mappings are persisted so downloads resume after a restart.</summary>
    public string StatePath { get; set; } = "state/active-jobs.json";
    /// <summary>If a torrent's progress hasn't advanced at all for this long, treat it as stalled/dead
    /// (no seeders) and fail it rather than polling forever.</summary>
    public int StallTimeoutMinutes { get; set; } = 120;
}

/// <summary>Indexer endpoints (public JSON APIs keyed by IMDb id).</summary>
public class IndexerOptions
{
    public const string Section = "Indexer";
    public int TimeoutSeconds { get; set; } = 20;
    public string EztvBaseUrl { get; set; } = "https://eztvx.to";   // TV (JSON API)
    public string YtsBaseUrl { get; set; } = "https://yts.mx";       // movies (JSON API)
    public string X1337xBaseUrl { get; set; } = "https://1337x.to";  // movies + TV (HTML scrape)
    public bool X1337xEnabled { get; set; } = true;
    /// <summary>How many result rows to open for magnet extraction per 1337x search (each is an extra request).</summary>
    public int X1337xMaxDetail { get; set; } = 8;

    // Nyaa — anime (RSS feed, no scraping).
    public string NyaaBaseUrl { get; set; } = "https://nyaa.si";
    public bool NyaaEnabled { get; set; } = true;
    /// <summary>Nyaa category: 1_2 = Anime English-translated, 1_0 = all anime.</summary>
    public string NyaaCategory { get; set; } = "1_2";

    // ext.to — general (HTML scrape). {query} is URL-substituted.
    public string ExtToBaseUrl { get; set; } = "https://ext.to";
    public bool ExtToEnabled { get; set; } = true;
    public string ExtToSearchPath { get; set; } = "/search/?q={query}&order=seeders&sort=desc";
    public int ExtToMaxDetail { get; set; } = 8;
}

/// <summary>Deluge Web (JSON-RPC) connection + label routing.</summary>
public class DelugeOptions
{
    public const string Section = "Deluge";
    public string Url { get; set; } = string.Empty; // e.g. http://localhost:8112
    public string Password { get; set; } = string.Empty;
    public string MovieLabel { get; set; } = "movies";
    public string TvLabel { get; set; } = "tv";
}

/// <summary>Where completed files are placed for Plex to index.</summary>
public class LibraryOptions
{
    public const string Section = "Library";
    public string MoviePath { get; set; } = string.Empty;
    public string TvPath { get; set; } = string.Empty;
    public bool Hardlink { get; set; } = true; // hardlink (keep seeding) vs move
}

/// <summary>Release-selection thresholds.</summary>
public class QualityOptions
{
    public const string Section = "Quality";
    public int MinSeeders { get; set; } = 1;
    public double MaxSizeGb { get; set; } = 25;
    public string[] PreferredGroups { get; set; } = Array.Empty<string>();
}

/// <summary>App-level VPN health check (defence in depth on top of the container kill-switch).</summary>
public class VpnOptions
{
    public const string Section = "Vpn";
    public bool Enabled { get; set; } = true;
    public string HealthCheckUrl { get; set; } = "https://api.ipify.org";
    public int TimeoutSeconds { get; set; } = 10;
    /// <summary>Optional: this host's own public IP (no VPN). If the health-check egress IP matches this,
    /// treat VPN as unhealthy even though the HTTP call itself succeeded — a weak but real signal the
    /// tunnel isn't actually active. Full VPN-tunnel verification is fundamentally an infra/network-
    /// namespace concern (a kill-switch at the container/network level); this is a best-effort code-level
    /// improvement on top of that, not a replacement for it.</summary>
    public string? ExpectedNonVpnIp { get; set; }
}
