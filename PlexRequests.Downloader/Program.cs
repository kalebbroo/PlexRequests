using PlexRequests.Downloader.Api;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Download;
using PlexRequests.Downloader.Import;
using PlexRequests.Downloader.Indexers;
using PlexRequests.Downloader.Organize;
using PlexRequests.Downloader.Ranking;
using PlexRequests.Downloader.Vpn;
using PlexRequests.Downloader.Worker;
using PlexRequestsHosted.Shared.Releases;

var builder = Host.CreateApplicationBuilder(args);

// Strongly-typed configuration (bindable from appsettings.json or env vars, e.g. Api__Key=...).
builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection(ApiOptions.Section));
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.Section));
builder.Services.Configure<IndexerOptions>(builder.Configuration.GetSection(IndexerOptions.Section));
builder.Services.Configure<CatalogWorkerOptions>(builder.Configuration.GetSection(CatalogWorkerOptions.Section));
builder.Services.Configure<DelugeOptions>(builder.Configuration.GetSection(DelugeOptions.Section));
builder.Services.Configure<DirectAudioOptions>(builder.Configuration.GetSection(DirectAudioOptions.Section));
builder.Services.Configure<LibraryOptions>(builder.Configuration.GetSection(LibraryOptions.Section));
builder.Services.Configure<QualityOptions>(builder.Configuration.GetSection(QualityOptions.Section));
builder.Services.Configure<VpnOptions>(builder.Configuration.GetSection(VpnOptions.Section));

// Typed client to the web app's fulfillment API; base URL + shared-secret header set here.
builder.Services.AddHttpClient<IPlexRequestsApiClient, PlexRequestsApiClient>((sp, http) =>
{
    var api = builder.Configuration.GetSection(ApiOptions.Section).Get<ApiOptions>() ?? new ApiOptions();
    http.BaseAddress = new Uri(api.BaseUrl);
    if (!string.IsNullOrWhiteSpace(api.Key))
        http.DefaultRequestHeaders.Add("X-Fulfillment-Key", api.Key);
    http.Timeout = TimeSpan.FromSeconds(30);
});

// Indexer providers (typed HttpClients) + aggregator.
var indexerCfg = builder.Configuration.GetSection(IndexerOptions.Section).Get<IndexerOptions>() ?? new IndexerOptions();

// Retry policy shared by every indexer client. There was none at all before, so a single transient 503,
// timeout or DNS blip counted as a hard failure for that indexer for the whole search pass.
// Deliberately does NOT retry 4xx: on a scraped site those mean blocked or rate-limited, and retrying
// makes that strictly worse.
void AddIndexerResilience(IHttpClientBuilder b) => b.AddStandardResilienceHandler(o =>
{
    o.Retry.MaxRetryAttempts = 3;
    o.Retry.UseJitter = true;
    o.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
    o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(Math.Max(5, indexerCfg.TimeoutSeconds));
    o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(Math.Max(20, indexerCfg.TimeoutSeconds * 3));
});
AddIndexerResilience(builder.Services.AddHttpClient<EztvIndexerProvider>(http =>
{
    http.BaseAddress = new Uri(indexerCfg.EztvBaseUrl);
    http.Timeout = TimeSpan.FromSeconds(indexerCfg.TimeoutSeconds);
    http.DefaultRequestHeaders.Add("User-Agent", "PlexRequests.Downloader");
}));
AddIndexerResilience(builder.Services.AddHttpClient<YtsIndexerProvider>(http =>
{
    // No fixed BaseAddress: YtsIndexerProvider tries each configured mirror (YtsBaseUrlsCsv) as an
    // absolute URL in turn, so one dead domain doesn't take movie search out entirely.
    http.Timeout = TimeSpan.FromSeconds(indexerCfg.TimeoutSeconds);
    http.DefaultRequestHeaders.Add("User-Agent", "PlexRequests.Downloader");
}));
// 1337x is scraped, so present a real browser User-Agent + Accept headers.
AddIndexerResilience(builder.Services.AddHttpClient<X1337xIndexerProvider>(http =>
{
    http.BaseAddress = new Uri(indexerCfg.X1337xBaseUrl);
    http.Timeout = TimeSpan.FromSeconds(indexerCfg.TimeoutSeconds);
    http.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
    http.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
}));
// Nyaa uses a plain RSS feed (a normal UA is fine).
AddIndexerResilience(builder.Services.AddHttpClient<NyaaIndexerProvider>(http =>
{
    http.BaseAddress = new Uri(indexerCfg.NyaaBaseUrl);
    http.Timeout = TimeSpan.FromSeconds(indexerCfg.TimeoutSeconds);
    http.DefaultRequestHeaders.Add("User-Agent", "PlexRequests.Downloader");
}));
// ext.to is scraped — present a real browser User-Agent.
AddIndexerResilience(builder.Services.AddHttpClient<ExtToIndexerProvider>(http =>
{
    http.BaseAddress = new Uri(indexerCfg.ExtToBaseUrl);
    http.Timeout = TimeSpan.FromSeconds(indexerCfg.TimeoutSeconds);
    http.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
    http.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
}));
// Pirate Bay JSON API (apibay) + torrents-csv — plain JSON endpoints, normal UA is fine.
AddIndexerResilience(builder.Services.AddHttpClient<PirateBayIndexerProvider>(http =>
{
    http.BaseAddress = new Uri(indexerCfg.PirateBayBaseUrl);
    http.Timeout = TimeSpan.FromSeconds(indexerCfg.TimeoutSeconds);
    http.DefaultRequestHeaders.Add("User-Agent", "PlexRequests.Downloader");
}));
AddIndexerResilience(builder.Services.AddHttpClient<TorrentsCsvIndexerProvider>(http =>
{
    http.BaseAddress = new Uri(indexerCfg.TorrentsCsvBaseUrl);
    http.Timeout = TimeSpan.FromSeconds(indexerCfg.TimeoutSeconds);
    http.DefaultRequestHeaders.Add("User-Agent", "PlexRequests.Downloader");
}));
// Torznab (Jackett/Prowlarr) — endpoints are absolute per-config URLs, so no BaseAddress here.
AddIndexerResilience(builder.Services.AddHttpClient<TorznabIndexerProvider>(http =>
{
    http.Timeout = TimeSpan.FromSeconds(indexerCfg.TimeoutSeconds);
    http.DefaultRequestHeaders.Add("User-Agent", "PlexRequests.Downloader");
}));
builder.Services.AddTransient<IIndexerImplementation>(sp => sp.GetRequiredService<TorznabIndexerProvider>());
builder.Services.AddTransient<IIndexerImplementation>(sp => sp.GetRequiredService<EztvIndexerProvider>());
builder.Services.AddTransient<IIndexerImplementation>(sp => sp.GetRequiredService<YtsIndexerProvider>());
builder.Services.AddTransient<IIndexerImplementation>(sp => sp.GetRequiredService<X1337xIndexerProvider>());
builder.Services.AddTransient<IIndexerImplementation>(sp => sp.GetRequiredService<NyaaIndexerProvider>());
builder.Services.AddTransient<IIndexerImplementation>(sp => sp.GetRequiredService<ExtToIndexerProvider>());
builder.Services.AddTransient<IIndexerImplementation>(sp => sp.GetRequiredService<PirateBayIndexerProvider>());
builder.Services.AddTransient<IIndexerImplementation>(sp => sp.GetRequiredService<TorrentsCsvIndexerProvider>());
builder.Services.AddTransient<IReleaseFeedSource>(sp => sp.GetRequiredService<NyaaIndexerProvider>());
builder.Services.AddTransient<IReleaseFeedSource>(sp => sp.GetRequiredService<EztvIndexerProvider>());
builder.Services.AddTransient<IReleaseFeedSource>(sp => sp.GetRequiredService<YtsIndexerProvider>());
builder.Services.AddTransient<IReleaseFeedSource>(sp => sp.GetRequiredService<TorznabIndexerProvider>());
builder.Services.AddTransient<IIndexerClient, IndexerClient>();
builder.Services.AddSingleton<IAcquisitionCandidateSource, YouTubeMusicDirectSource>();
// Per-(indexer, query) result cache and the per-indexer request throttle the client sits on top of.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IIndexerRateLimiter, IndexerRateLimiter>();
// The single fetch seam every scraped indexer goes through: applies stored clearance credentials and turns
// a refusal into a typed IndexerBlockedException instead of an empty result set.
builder.Services.AddSingleton<IIndexerFetch, IndexerFetch>();
// One-time migration of Indexer__Torznab__* env config into the admin-managed Indexers table.
builder.Services.AddHostedService<PlexRequests.Downloader.Worker.LegacyTorznabImporter>();
// Serves admin-initiated searches on a short poll, separate from the fulfillment loop — someone is waiting.
builder.Services.AddHostedService<PlexRequests.Downloader.Worker.InteractiveSearchWorker>();
// Reconciles the database's view of what is downloading against the client's, every cycle. Stateless by
// design: a restart loses nothing because it holds nothing between passes.
builder.Services.AddHostedService<PlexRequests.Downloader.Worker.TransferReconciler>();
// One-time-per-start bridge: without it the reconciler cannot see anything that was already downloading
// when it shipped — which is exactly the set it exists to rescue.
builder.Services.AddHostedService<PlexRequests.Downloader.Worker.LegacyTorrentStateAdopter>();
// Browses enabled indexers' newest uploads to feed the home page's "Recommended" row. Runs here rather
// than in the web app so torrent-site traffic stays inside the VPN tunnel.
builder.Services.AddHostedService<PlexRequests.Downloader.Worker.RecommendedFeedWorker>();
// Watches indexers for anything currently wanted — the low-latency safety net behind the air-date estimate.
builder.Services.AddHostedService<PlexRequests.Downloader.Worker.RssSweepWorker>();
// Durable, source-once catalog ingestion. Search and monitoring reads have separate rollout flags.
builder.Services.AddHostedService<PlexRequests.Downloader.Worker.ReleaseIngestionWorker>();

// Admin-configured download preferences, fetched from the web app (appsettings QualityOptions fallback).
builder.Services.AddSingleton<IDownloadPreferencesProvider, DownloadPreferencesProvider>();
// Admin per-indexer enable/priority (the web Indexers panel); unknown/unreachable defaults to enabled.
builder.Services.AddSingleton<IIndexerSettingsProvider, IndexerSettingsProvider>();
// Admin-configured library organization (paths, naming templates, transfer mode), same fetch/fallback pattern.
builder.Services.AddSingleton<ILibraryOrganizationProvider, LibraryOrganizationPreferencesProvider>();

// Release parsing + ranking.
builder.Services.AddSingleton<IReleaseParser, ReleaseParser>();
// Ranking logic lives in Shared so the web app can run the identical evaluation for interactive search;
// the adapter binds it to this process's config and logging.
builder.Services.AddSingleton<IReleaseEvaluator, ReleaseEvaluator>();
builder.Services.AddSingleton<IDownloadPlanner, DownloadPlanner>();
builder.Services.AddSingleton<IReleaseRanker, ReleaseRankerAdapter>();

// Deluge client — shared CookieContainer keeps the session across handler rotations.
var delugeCfg = builder.Configuration.GetSection(DelugeOptions.Section).Get<DelugeOptions>() ?? new DelugeOptions();
var delugeCookies = new System.Net.CookieContainer();
builder.Services.AddHttpClient<IDownloadClient, DelugeDownloadClient>(http =>
{
    if (!string.IsNullOrWhiteSpace(delugeCfg.Url)) http.BaseAddress = new Uri(delugeCfg.Url);
    http.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    CookieContainer = delugeCookies,
    UseCookies = true
});
builder.Services.AddHttpClient(nameof(DirectAudioMediaEnricher), http =>
{
    http.Timeout = TimeSpan.FromSeconds(30);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("PlexRequests/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseCookies = false
});
builder.Services.AddSingleton<IDirectAudioMediaEnricher, DirectAudioMediaEnricher>();
builder.Services.AddSingleton<IAcquisitionBackend, TorrentAcquisitionBackend>();
builder.Services.AddSingleton<IAcquisitionBackend, YouTubeMusicAcquisitionBackend>();
builder.Services.AddSingleton<IAcquisitionBackendRegistry, AcquisitionBackendRegistry>();

// Library organizer: archive extraction, season-pack splitting, Plex-convention naming/transfer.
builder.Services.AddSingleton<IArchiveExtractor, ArchiveExtractor>();
builder.Services.AddSingleton<ISeasonPackSplitter, SeasonPackSplitter>();
builder.Services.AddSingleton<IPlexNamingService, PlexNamingService>();
builder.Services.AddSingleton<IEpisodeTitleProvider, EpisodeTitleProvider>();
builder.Services.AddSingleton<ILibraryOrganizer, LibraryOrganizer>();
builder.Services.AddSingleton<ILibraryImporter, LibraryImporter>();
// The legacy job monitor and the durable reconciler can see the same completed torrent. They share this
// single-flight boundary so exactly one physical library import occurs and both callers receive its result.
builder.Services.AddSingleton<ITransferImportCoordinator, TransferImportCoordinator>();
builder.Services.AddHttpClient<IVpnGuard, VpnGuard>();

// Pipeline + restart-resumable state + the orchestrating worker.
builder.Services.AddSingleton<IJobStateStore, JsonJobStateStore>();
builder.Services.AddSingleton<IFulfillmentPipeline, FulfillmentPipeline>();
builder.Services.AddHostedService<FulfillmentWorker>();
// Mounts admin-configured NAS/network shares (read-write) so the organizer can place files there.
builder.Services.AddHostedService<NetworkMountService>();

var host = builder.Build();
host.Run();
