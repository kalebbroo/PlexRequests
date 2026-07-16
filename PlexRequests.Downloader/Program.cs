using PlexRequests.Downloader.Api;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Download;
using PlexRequests.Downloader.Import;
using PlexRequests.Downloader.Indexers;
using PlexRequests.Downloader.Organize;
using PlexRequests.Downloader.Ranking;
using PlexRequests.Downloader.Vpn;
using PlexRequests.Downloader.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Strongly-typed configuration (bindable from appsettings.json or env vars, e.g. Api__Key=...).
builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection(ApiOptions.Section));
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.Section));
builder.Services.Configure<IndexerOptions>(builder.Configuration.GetSection(IndexerOptions.Section));
builder.Services.Configure<DelugeOptions>(builder.Configuration.GetSection(DelugeOptions.Section));
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
builder.Services.AddHttpClient<EztvIndexerProvider>(http =>
{
    http.BaseAddress = new Uri(indexerCfg.EztvBaseUrl);
    http.Timeout = TimeSpan.FromSeconds(indexerCfg.TimeoutSeconds);
    http.DefaultRequestHeaders.Add("User-Agent", "PlexRequests.Downloader");
});
builder.Services.AddHttpClient<YtsIndexerProvider>(http =>
{
    // No fixed BaseAddress: YtsIndexerProvider tries each configured mirror (YtsBaseUrlsCsv) as an
    // absolute URL in turn, so one dead domain doesn't take movie search out entirely.
    http.Timeout = TimeSpan.FromSeconds(indexerCfg.TimeoutSeconds);
    http.DefaultRequestHeaders.Add("User-Agent", "PlexRequests.Downloader");
});
// 1337x is scraped, so present a real browser User-Agent + Accept headers.
builder.Services.AddHttpClient<X1337xIndexerProvider>(http =>
{
    http.BaseAddress = new Uri(indexerCfg.X1337xBaseUrl);
    http.Timeout = TimeSpan.FromSeconds(indexerCfg.TimeoutSeconds);
    http.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
    http.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
});
// Nyaa uses a plain RSS feed (a normal UA is fine).
builder.Services.AddHttpClient<NyaaIndexerProvider>(http =>
{
    http.BaseAddress = new Uri(indexerCfg.NyaaBaseUrl);
    http.Timeout = TimeSpan.FromSeconds(indexerCfg.TimeoutSeconds);
    http.DefaultRequestHeaders.Add("User-Agent", "PlexRequests.Downloader");
});
// ext.to is scraped — present a real browser User-Agent.
builder.Services.AddHttpClient<ExtToIndexerProvider>(http =>
{
    http.BaseAddress = new Uri(indexerCfg.ExtToBaseUrl);
    http.Timeout = TimeSpan.FromSeconds(indexerCfg.TimeoutSeconds);
    http.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
    http.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
});
builder.Services.AddTransient<IIndexerProvider>(sp => sp.GetRequiredService<EztvIndexerProvider>());
builder.Services.AddTransient<IIndexerProvider>(sp => sp.GetRequiredService<YtsIndexerProvider>());
builder.Services.AddTransient<IIndexerProvider>(sp => sp.GetRequiredService<X1337xIndexerProvider>());
builder.Services.AddTransient<IIndexerProvider>(sp => sp.GetRequiredService<NyaaIndexerProvider>());
builder.Services.AddTransient<IIndexerProvider>(sp => sp.GetRequiredService<ExtToIndexerProvider>());
builder.Services.AddTransient<IIndexerClient, IndexerClient>();

// Admin-configured download preferences, fetched from the web app (appsettings QualityOptions fallback).
builder.Services.AddSingleton<IDownloadPreferencesProvider, DownloadPreferencesProvider>();
// Admin-configured library organization (paths, naming templates, transfer mode), same fetch/fallback pattern.
builder.Services.AddSingleton<ILibraryOrganizationProvider, LibraryOrganizationPreferencesProvider>();

// Release parsing + ranking.
builder.Services.AddSingleton<IReleaseParser, ReleaseParser>();
builder.Services.AddSingleton<IReleaseRanker, ReleaseRanker>();

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

// Library organizer: archive extraction, season-pack splitting, Plex-convention naming/transfer.
builder.Services.AddSingleton<IArchiveExtractor, ArchiveExtractor>();
builder.Services.AddSingleton<ISeasonPackSplitter, SeasonPackSplitter>();
builder.Services.AddSingleton<IPlexNamingService, PlexNamingService>();
builder.Services.AddSingleton<IEpisodeTitleProvider, EpisodeTitleProvider>();
builder.Services.AddSingleton<ILibraryOrganizer, LibraryOrganizer>();
builder.Services.AddSingleton<ILibraryImporter, LibraryImporter>();
builder.Services.AddHttpClient<IVpnGuard, VpnGuard>();

// Pipeline + restart-resumable state + the orchestrating worker.
builder.Services.AddSingleton<IJobStateStore, JsonJobStateStore>();
builder.Services.AddSingleton<IFulfillmentPipeline, FulfillmentPipeline>();
builder.Services.AddHostedService<FulfillmentWorker>();
// Mounts admin-configured NAS/network shares (read-write) so the organizer can place files there.
builder.Services.AddHostedService<NetworkMountService>();

var host = builder.Build();
host.Run();
