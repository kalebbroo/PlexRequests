using PlexRequests.Downloader.Api;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Download;
using PlexRequests.Downloader.Import;
using PlexRequests.Downloader.Indexers;
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
    http.BaseAddress = new Uri(indexerCfg.YtsBaseUrl);
    http.Timeout = TimeSpan.FromSeconds(indexerCfg.TimeoutSeconds);
    http.DefaultRequestHeaders.Add("User-Agent", "PlexRequests.Downloader");
});
builder.Services.AddTransient<IIndexerProvider>(sp => sp.GetRequiredService<EztvIndexerProvider>());
builder.Services.AddTransient<IIndexerProvider>(sp => sp.GetRequiredService<YtsIndexerProvider>());
builder.Services.AddTransient<IIndexerClient, IndexerClient>();

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

// Library import + VPN health guard.
builder.Services.AddSingleton<ILibraryImporter, LibraryImporter>();
builder.Services.AddHttpClient<IVpnGuard, VpnGuard>();

// Pipeline + restart-resumable state + the orchestrating worker.
builder.Services.AddSingleton<IJobStateStore, JsonJobStateStore>();
builder.Services.AddSingleton<IFulfillmentPipeline, FulfillmentPipeline>();
builder.Services.AddHostedService<FulfillmentWorker>();

var host = builder.Build();
host.Run();
