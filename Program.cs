using PlexRequestsHosted.Components;
using MudBlazor.Services;
using Blazored.LocalStorage;
using Blazored.SessionStorage;
using Microsoft.AspNetCore.Components.Authorization;
using PlexRequestsHosted.Services.Auth;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Services.MetadataProviders;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Media;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using PlexRequestsHosted.Utils;
using System.Threading.RateLimiting;

// Load .env if present and map PLEX_* variables to ASP.NET config keys
static void LoadDotEnvFrom(string rootPath)
{
    var candidates = new[]
    {
        Path.Combine(rootPath, ".env"),
        Path.Combine(AppContext.BaseDirectory, ".env"),
        Path.Combine(Directory.GetCurrentDirectory(), ".env")
    };
    var path = candidates.FirstOrDefault(File.Exists);
    if (path is null) return;
    foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
    {
        var line = raw.Trim();
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
        var idx = line.IndexOf('=');
        if (idx <= 0) continue;
        var key = line[..idx].Trim();
        var val = line[(idx + 1)..].Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(key)) continue;
        Environment.SetEnvironmentVariable(key, val);
        // Map friendly keys to ASP.NET configuration keys
        if (key.Equals("PLEX_URL", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("Plex__PrimaryServerUrl", val);
        else if (key.Equals("PLEX_TOKEN", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("Plex__ServerToken", val);
        else if (key.Equals("PLEX_CLIENT_IDENTIFIER", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("Plex__ClientIdentifier", val);
        else if (key.Equals("PLEX_ALLOW_INVALID_CERTS", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("Plex__AllowInvalidCerts", val);
        else if (key.Equals("TMDB_API_KEY", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("ApiKeys__TMDb__ApiKey", val);
        else if (key.Equals("TMDB_READ_ACCESS_TOKEN", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("ApiKeys__TMDb__ReadAccessToken", val);
        else if (key.Equals("ADMIN_USERNAMES", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("Admin__Usernames", val);
        else if (key.Equals("DB_PATH", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("ConnectionStrings__AppDb", val);
        else if (key.Equals("FULFILLMENT_ENABLED", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("Fulfillment__Enabled", val);
        else if (key.Equals("FULFILLMENT_API_KEY", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("Fulfillment__ApiKey", val);
        else if (key.Equals("BRIDGE_ENABLED", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("Bridge__Enabled", val);
        else if (key.Equals("BRIDGE_API_KEY", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("Bridge__ApiKey", val);
        else if (key.Equals("BRIDGE_EVENT_RETENTION_DAYS", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("Bridge__EventRetentionDays", val);
        else if (key.Equals("DEV_AUTH_ENABLED", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("DevelopmentAuth__Enabled", val);
        else if (key.Equals("DEV_AUTH_USERNAME", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("DevelopmentAuth__Username", val);
        else if (key.Equals("DEV_AUTH_DISPLAY_NAME", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("DevelopmentAuth__DisplayName", val);
        else if (key.Equals("DEV_AUTH_EMAIL", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("DevelopmentAuth__Email", val);
        else if (key.Equals("DEV_AUTH_AVATAR_URL", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("DevelopmentAuth__AvatarUrl", val);
        else if (key.Equals("DEV_AUTH_ROLES", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("DevelopmentAuth__Roles", val);
        else if (key.Equals("DEV_AUTH_TOKEN", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("DevelopmentAuth__Token", val);
    }
}

// Preload .env before configuration is built so env vars flow into builder.Configuration
LoadDotEnvFrom(Directory.GetCurrentDirectory());

var builder = WebApplication.CreateBuilder(args);
// Also attempt loading after, using content root
LoadDotEnvFrom(builder.Environment.ContentRootPath);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// UI and storage services for client interactivity
builder.Services.AddMudServices();
builder.Services.AddBlazoredLocalStorage();
builder.Services.AddBlazoredSessionStorage();
// HTTP client for services that depend on HttpClient
builder.Services.AddHttpClient();
// HttpContext accessor for cookie sign-in from AuthStateProvider
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);

// Public extension endpoints authenticate with single-purpose device tokens rather than the browser's
// application cookie. Bound abusive guessing and upload loops before either reaches SQLite.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("firefox-capture-pair", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("firefox-capture-ingest", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

// Behind a reverse proxy / Cloudflare Tunnel (TLS at the edge, plain HTTP to the origin): trust the
// forwarded scheme so HttpsRedirection doesn't loop, the auth cookie gets its Secure flag, and Plex
// OAuth redirect URLs come out as https. cloudflared/the proxy is the only origin client, so trust
// all proxies. If you expose the origin directly to untrusted networks, restrict KnownProxies instead.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
// Session support for OAuth PIN storage
// Persist Data Protection keys so session/cookie protection can be unprotected across app restarts
var keysDir = Path.Combine(builder.Environment.ContentRootPath, "keys");
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
    .SetApplicationName("PlexRequestsHosted");
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(20);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// Options/config
builder.Services.Configure<PlexRequestsHosted.Services.Implementations.PlexConfiguration>(
    builder.Configuration.GetSection("Plex"));
builder.Services.AddMemoryCache();

// Core domain services
// Configure typed HttpClient for PlexApiService with optional invalid cert allowance (for self-signed or IP-based SSL)
var plexSection = builder.Configuration.GetSection("Plex");
var allowInvalidCerts = plexSection.GetValue<bool>("AllowInvalidCerts");
// Cap the per-request timeout so a slow/stalled Plex call can never freeze a Blazor render for the
// default 100s. Plex service methods catch the resulting cancellation and degrade to "unavailable".
builder.Services.AddHttpClient<IPlexApiService, PlexApiService>(c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) =>
            allowInvalidCerts ? true : errors == SslPolicyErrors.None
    });
builder.Services.AddHttpClient<IPlexArtworkService, PlexArtworkService>(c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) =>
            allowInvalidCerts ? true : errors == SslPolicyErrors.None
    });
// Plex music library access (artist/album/track) — foundation for music requests.
builder.Services.AddHttpClient<PlexRequestsHosted.Services.Implementations.IPlexMusicService, PlexRequestsHosted.Services.Implementations.PlexMusicService>(c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) =>
            allowInvalidCerts ? true : errors == SslPolicyErrors.None
    });
builder.Services.AddScoped<IMediaRequestService, MediaRequestService>();
builder.Services.AddScoped<IMediaIdentityService, MediaIdentityService>();
builder.Services.AddScoped<IMusicSettingsService, MusicSettingsService>();
builder.Services.AddScoped<IMusicDirectAcquisitionResolver, YouTubeMusicDirectAcquisitionResolver>();
builder.Services.AddSingleton<IMediaModule, MovieMediaModule>();
builder.Services.AddSingleton<IMediaModule, TelevisionMediaModule>();
builder.Services.AddSingleton<IMediaModule, AnimeMediaModule>();
builder.Services.AddSingleton<IMediaModule, MusicMediaModule>();
builder.Services.AddSingleton<IMediaModuleRegistry, MediaModuleRegistry>();
builder.Services.AddScoped<PlexRequestsHosted.Services.Abstractions.ISeasonAvailabilityEvaluator, PlexRequestsHosted.Services.Implementations.SeasonAvailabilityEvaluator>();
builder.Services.AddScoped<IFulfillmentQueue, FulfillmentQueue>();
// Live download telemetry: the store is a singleton (shared by worker progress reports and the admin
// circuit); the read model service is scoped (needs the DbContext).
builder.Services.AddSingleton<PlexRequestsHosted.Services.Abstractions.IDownloadTelemetryStore, PlexRequestsHosted.Services.Implementations.DownloadTelemetryStore>();
builder.Services.AddScoped<PlexRequestsHosted.Services.Abstractions.IDownloadMonitorService, PlexRequestsHosted.Services.Implementations.DownloadMonitorService>();
builder.Services.AddScoped<IDiscordLinkService, DiscordLinkService>();
builder.Services.AddScoped<IBridgeOutboxService, BridgeOutboxService>();
builder.Services.AddScoped<PlexRequestsHosted.Services.Implementations.IMediaIssueService, PlexRequestsHosted.Services.Implementations.MediaIssueService>();
builder.Services.AddScoped<PlexRequestsHosted.Services.Implementations.IQualityRuleService, PlexRequestsHosted.Services.Implementations.QualityRuleService>();
builder.Services.AddScoped<PlexRequestsHosted.Services.Implementations.IDownloadPreferencesService, PlexRequestsHosted.Services.Implementations.DownloadPreferencesService>();
builder.Services.AddScoped<PlexRequestsHosted.Services.Implementations.ILibraryOrganizationPreferencesService, PlexRequestsHosted.Services.Implementations.LibraryOrganizationPreferencesService>();
builder.Services.AddSingleton<PlexRequestsHosted.Services.Implementations.IFolderBrowserService, PlexRequestsHosted.Services.Implementations.FolderBrowserService>();
// Network shares (NAS/network drives): CRUD service + live mount-status store + the background service
// that mounts them read-only so the folder browser can list them. The same instance is exposed as
// INetworkMountController so an admin save/test can trigger an immediate reconcile.
builder.Services.AddScoped<PlexRequestsHosted.Services.Implementations.INetworkShareService, PlexRequestsHosted.Services.Implementations.NetworkShareService>();
builder.Services.AddSingleton<PlexRequestsHosted.Services.Background.INetworkMountStatusStore, PlexRequestsHosted.Services.Background.NetworkMountStatusStore>();
builder.Services.AddSingleton<PlexRequestsHosted.Services.Background.WebNetworkMountService>();
builder.Services.AddSingleton<PlexRequestsHosted.Services.Background.INetworkMountController>(sp => sp.GetRequiredService<PlexRequestsHosted.Services.Background.WebNetworkMountService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<PlexRequestsHosted.Services.Background.WebNetworkMountService>());
// Generic background-job engine + its handlers. The scheduler ticks, dispatches due jobs to the matching
// IJobHandler, and records run history. Every recurring job lives here — the four below used to be
// self-timing BackgroundServices with hardcoded Task.Delay loops, which meant no run history, no
// enable/disable, no "Run now" and no visibility in the admin Jobs panel. Their cadence now lives in
// their ScheduledJobEntity row.
builder.Services.AddScoped<PlexRequestsHosted.Services.Jobs.IJobHandler, PlexRequestsHosted.Services.Jobs.MissingSearchJob>();
builder.Services.AddScoped<PlexRequestsHosted.Services.Jobs.IJobHandler, PlexRequestsHosted.Services.Jobs.SearchTaskCleanupJob>();
// The durable job<->torrent link the reconciler joins on. Without it the link lived only in the worker's
// local file and an in-memory store, so a worker restart orphaned every in-flight download.
builder.Services.AddScoped<PlexRequestsHosted.Services.Implementations.IFulfillmentTransferService, PlexRequestsHosted.Services.Implementations.FulfillmentTransferService>();
// Re-derives tier + format score for already-imported files from their stored release names, so editing a
// custom format reaches the library you already have and not only the next download.
builder.Services.AddScoped<PlexRequestsHosted.Services.Jobs.IJobHandler, PlexRequestsHosted.Services.Jobs.RecomputeFormatScoresJob>();
// Air-date calendar: one job keeps it current from live metadata, the other queues searches when an
// episode's window opens and tells the scheduler when the next one is due.
builder.Services.AddScoped<PlexRequestsHosted.Services.Jobs.IJobHandler, PlexRequestsHosted.Services.Jobs.CalendarRefreshJob>();
builder.Services.AddScoped<PlexRequestsHosted.Services.Jobs.IJobHandler, PlexRequestsHosted.Services.Jobs.AirDateMonitorJob>();
builder.Services.AddScoped<PlexRequestsHosted.Services.Implementations.IMonitoringPreferencesService, PlexRequestsHosted.Services.Implementations.MonitoringPreferencesService>();
// Per-series monitoring controls — the first way anything can change Monitored after a request is created.
builder.Services.AddScoped<PlexRequestsHosted.Services.Implementations.ISeriesMonitoringService, PlexRequestsHosted.Services.Implementations.SeriesMonitoringService>();
// User-defined release scoring rules, scored per quality profile.
builder.Services.AddScoped<PlexRequestsHosted.Services.Implementations.ICustomFormatService, PlexRequestsHosted.Services.Implementations.CustomFormatService>();
// Registered concretely as well as via IJobHandler: the admin "Upgrade now" action calls straight into it
// rather than keeping a second copy of the same logic.
builder.Services.AddScoped<PlexRequestsHosted.Services.Jobs.UpgradeScanJob>();
builder.Services.AddScoped<PlexRequestsHosted.Services.Jobs.IJobHandler>(sp => sp.GetRequiredService<PlexRequestsHosted.Services.Jobs.UpgradeScanJob>());
// Backstop that requeues/parks jobs stranded by a dead downloader.
builder.Services.AddScoped<PlexRequestsHosted.Services.Jobs.IJobHandler, PlexRequestsHosted.Services.Background.FulfillmentReaperService>();
// Keeps the DB-backed Plex availability index fresh (per-season episode presence + aged-out removals).
builder.Services.AddScoped<PlexRequestsHosted.Services.Jobs.IJobHandler, PlexRequestsHosted.Services.Background.AvailabilityRefreshService>();
// Safety net: auto-mark requests Available when their content appears on Plex by ANY means.
builder.Services.AddScoped<PlexRequestsHosted.Services.Jobs.IJobHandler, PlexRequestsHosted.Services.Background.AvailabilityReconciliationService>();
builder.Services.AddScoped<PlexRequestsHosted.Services.Abstractions.IJobAdminService, PlexRequestsHosted.Services.Jobs.JobAdminService>();
// Quality tiers/profiles: seeds the catalog + stock profiles and converts the legacy quality rules.
builder.Services.AddScoped<PlexRequestsHosted.Services.Implementations.QualityProfileSeeder>();
builder.Services.AddScoped<PlexRequestsHosted.Services.Implementations.IQualityProfileService, PlexRequestsHosted.Services.Implementations.QualityProfileService>();
// Interactive search + the failure blocklist that stops a retry re-grabbing the same broken torrent.
builder.Services.AddScoped<PlexRequestsHosted.Services.Implementations.IInteractiveSearchService, PlexRequestsHosted.Services.Implementations.InteractiveSearchService>();
builder.Services.AddScoped<PlexRequestsHosted.Services.Implementations.IReleaseBlocklistService, PlexRequestsHosted.Services.Implementations.ReleaseBlocklistService>();
// Resolves the downloader's trending-title feed to real metadata for the home page's Recommended row.
builder.Services.AddScoped<PlexRequestsHosted.Services.Implementations.IRecommendedFeedService, PlexRequestsHosted.Services.Implementations.RecommendedFeedService>();
// Per-indexer admin control (enable/priority) + rolling health from downloader search telemetry.
builder.Services.AddScoped<PlexRequestsHosted.Services.Abstractions.IIndexerAdminService, PlexRequestsHosted.Services.Implementations.IndexerAdminService>();
builder.Services.Configure<PlexRequestsHosted.Infrastructure.Capture.FirefoxCaptureOptions>(
    builder.Configuration.GetSection(PlexRequestsHosted.Infrastructure.Capture.FirefoxCaptureOptions.Section));
builder.Services.AddScoped<PlexRequestsHosted.Services.Abstractions.IFirefoxCaptureService,
    PlexRequestsHosted.Services.Implementations.FirefoxCaptureService>();
// Optional, rebuildable release catalog. It is a singleton writer over short-lived contexts so concurrent
// worker batches cannot race unique infohash/source constraints. Disabled by default during shadow rollout.
builder.Services.Configure<PlexRequestsHosted.Infrastructure.Catalog.CatalogOptions>(
    builder.Configuration.GetSection(PlexRequestsHosted.Infrastructure.Catalog.CatalogOptions.Section));
builder.Services.AddSingleton<PlexRequestsHosted.Shared.Releases.IReleaseParser,
    PlexRequestsHosted.Shared.Releases.ReleaseParser>();
builder.Services.AddSingleton<PlexRequestsHosted.Services.Abstractions.IReleaseCatalogService,
    PlexRequestsHosted.Services.Implementations.ReleaseCatalogService>();
// Database maintenance: overview/backup/targeted cleanup/factory reset (the System → Database panel).
builder.Services.AddScoped<PlexRequestsHosted.Services.Abstractions.IDatabaseAdminService, PlexRequestsHosted.Services.Implementations.DatabaseAdminService>();
builder.Services.AddHostedService<PlexRequestsHosted.Services.Background.JobSchedulerService>();
builder.Services.AddHostedService<PlexRequestsHosted.Services.Background.CatalogMaintenanceWorker>();

// AuthN/AuthZ
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = "Cookies";
        options.DefaultAuthenticateScheme = "Cookies";
        options.DefaultChallengeScheme = "Cookies";
    })
    .AddCookie("Cookies", o =>
    {
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/login";
        o.LogoutPath = "/logout";
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.Name = "PlexRequestsAuth";
        o.ReturnUrlParameter = "returnUrl";
        o.Events.OnRedirectToLogin = context =>
        {
            // Prevent redirect loops by checking if already on login page
            if (context.Request.Path.StartsWithSegments("/login"))
            {
                context.Response.StatusCode = 401;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = 403;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options =>
{
    // Pages are secured by default via `@attribute [Authorize]` in Components/_Imports.razor
    // (enforced by AuthorizeRouteView); anonymous pages opt out with [AllowAnonymous].
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});
builder.Services.AddScoped<AuthenticationStateProvider, CustomAuthStateProvider>();
// Register the concrete type as well for endpoints that take CustomAuthStateProvider directly
builder.Services.AddScoped<CustomAuthStateProvider>();

// App service registrations (stubs for now)
// (Removed duplicate registrations of IPlexApiService/IMediaRequestService)
builder.Services.AddScoped<IPlexAuthService, PlexAuthService>();
builder.Services.AddScoped<IUserProfileService, UserProfileService>();
builder.Services.AddScoped<IUserAccessService, UserAccessService>();
builder.Services.AddScoped<IUserAdministrationService, UserAdministrationService>();
builder.Services.AddScoped<UserGroupSeeder>();
builder.Services.AddScoped<IToastService, ToastService>();
builder.Services.AddScoped<IThemeService, ThemeService>();
builder.Services.AddScoped<IAuthService, AuthService>();
// In-process notification pub/sub (replaces the SignalR client round-trip) + persistence-backed service
builder.Services.AddSingleton<PlexRequestsHosted.Services.Abstractions.INotificationBroker, PlexRequestsHosted.Services.Implementations.NotificationBroker>();
builder.Services.AddSingleton<PlexRequestsHosted.Services.Abstractions.INotificationService, PlexRequestsHosted.Services.Implementations.NotificationService>();

// Metadata providers (modular; the router picks one per media type with a keyless fallback).
// Singleton so its TMDbClient (and internal HttpClient) is built once, not per scope.
builder.Services.AddSingleton<TmdbMetadataProvider>();
builder.Services.AddScoped<TraktMetadataProvider>();
builder.Services.AddScoped<SeedMetadataProvider>();
builder.Services.AddScoped<TvdbMetadataProvider>();
// MusicBrainz requires a descriptive User-Agent; keyless -> default fallback for Music.
builder.Services.AddSingleton<IMusicBrainzRateGate, MusicBrainzRateGate>();
builder.Services.AddHttpClient<IListenBrainzDiscoveryClient, ListenBrainzDiscoveryClient>(c =>
{
    c.BaseAddress = new Uri("https://api.listenbrainz.org/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("PlexRequests/1.0 (https://github.com/kalebbroo/PlexRequests)");
    c.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<MusicBrainzMetadataProvider>(c =>
{
    c.BaseAddress = new Uri("https://musicbrainz.org/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("PlexRequests/1.0 (https://github.com/kalebbroo/PlexRequests)");
    c.Timeout = TimeSpan.FromSeconds(20);
});
// Anonymous, read-only YouTube Music web catalog client. This never requests or consumes media streams;
// acquisition remains the downloader's independent indexer responsibility.
builder.Services.AddHttpClient<YouTubeMusicMetadataProvider>(c =>
{
    c.BaseAddress = new Uri("https://music.youtube.com/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (X11; Linux x86_64; rv:142.0) Gecko/20100101 Firefox/142.0");
    c.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddScoped<IMusicDiscoveryService, MusicDiscoveryService>();
builder.Services.AddScoped<MetadataRouter>();
builder.Services.AddScoped<IMetadataProviderFactory, MetadataProviderFactory>();

// Background refresher for stale metadata-cache rows (stale-while-revalidate).
builder.Services.AddSingleton<PlexRequestsHosted.Services.Abstractions.IMetadataRefreshCoordinator,
    PlexRequestsHosted.Services.Implementations.MetadataRefreshCoordinator>();

// The active provider wrapped in a DB-backed caching decorator: details/imdb/episodes are served from
// SQLite instantly and survive restarts; stale rows refresh in the background.
builder.Services.AddScoped<IMediaMetadataProvider>(sp =>
{
    var innerProvider = sp.GetRequiredService<IMetadataProviderFactory>().GetDefaultProvider();
    return new PlexRequestsHosted.Services.Implementations.CachingMetadataProvider(
        innerProvider,
        sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
        sp.GetRequiredService<PlexRequestsHosted.Services.Abstractions.IMetadataRefreshCoordinator>(),
        sp.GetRequiredService<IMediaIdentityService>());
});

// Persistence: SQLite. Resolve an absolute path so the DB doesn't depend on the current
// working directory (DB_PATH / ConnectionStrings:AppDb override; default is under the content root).
var configuredDbPath = builder.Configuration["ConnectionStrings:AppDb"];
var dbPath = string.IsNullOrWhiteSpace(configuredDbPath)
    ? Path.Combine(builder.Environment.ContentRootPath, "app.db")
    : (Path.IsPathRooted(configuredDbPath)
        ? configuredDbPath
        : Path.Combine(builder.Environment.ContentRootPath, configuredDbPath));
// Factory registration + a scoped shim: existing scoped consumers keep injecting AppDbContext, while
// the caching layer + background refreshers create their own short-lived, thread-safe contexts.
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}")
           .AddInterceptors(new PlexRequestsHosted.Infrastructure.Data.SqlitePragmaInterceptor()));
builder.Services.AddScoped<AppDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

// Catalog persistence is deliberately separate from app.db: it is bounded derived data that can be
// pruned or rebuilt without touching requests, users, jobs, or application backups.
var configuredCatalogDbPath = builder.Configuration["ConnectionStrings:CatalogDb"];
var catalogDbPath = string.IsNullOrWhiteSpace(configuredCatalogDbPath)
    ? Path.Combine(Path.GetDirectoryName(dbPath) ?? builder.Environment.ContentRootPath, "catalog.db")
    : (Path.IsPathRooted(configuredCatalogDbPath)
        ? configuredCatalogDbPath
        : Path.Combine(builder.Environment.ContentRootPath, configuredCatalogDbPath));
builder.Services.AddDbContextFactory<PlexRequestsHosted.Infrastructure.Catalog.CatalogDbContext>(options =>
    options.UseSqlite($"Data Source={catalogDbPath}")
        .AddInterceptors(new PlexRequestsHosted.Infrastructure.Data.SqlitePragmaInterceptor()));

var app = builder.Build();

// Apply forwarded headers first so every downstream component (HSTS, HttpsRedirection, auth cookie,
// OAuth URL building) sees the real client scheme/IP from the proxy.
app.UseForwardedHeaders();
app.UseRateLimiter();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

// Serve static files before authentication to prevent JS/CSS from being blocked
app.UseStaticFiles();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Apply EF Core migrations on startup (creates the schema on first run, upgrades it thereafter).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    var catalogOptions = scope.ServiceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<PlexRequestsHosted.Infrastructure.Catalog.CatalogOptions>>()
        .Value;
    if (catalogOptions.Enabled)
    {
        var catalog = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<PlexRequestsHosted.Infrastructure.Catalog.CatalogDbContext>>();
        await using var catalogDb = await catalog.CreateDbContextAsync();
        await catalogDb.Database.MigrateAsync();
    }

    // Seed the quality-tier catalog, the stock profiles, and (first run only) convert the legacy quality
    // rules into profiles + assignment rules, backfilling everything that needs a profile. Idempotent, so
    // it runs on every boot and does nothing once complete. Must follow Migrate() — it writes to the tables
    // that migration creates. A failure here is logged and swallowed: a seeding problem shouldn't stop the
    // app from starting, and the next boot retries.
    try
    {
        await scope.ServiceProvider.GetRequiredService<UserGroupSeeder>().SeedAsync();
        await scope.ServiceProvider.GetRequiredService<PlexRequestsHosted.Services.Implementations.QualityProfileSeeder>()
            .SeedAsync();
        // Stock custom formats, so the panel is populated and the feature is usable on first boot rather
        // than only after something happens to touch the service.
        await scope.ServiceProvider.GetRequiredService<PlexRequestsHosted.Services.Implementations.ICustomFormatService>()
            .SeedAsync();
        // Repair torrents written off as Missing that had in fact been imported — see the note on
        // CorrectMisclassifiedMissingAsync. Idempotent, so it costs one query once the data is clean.
        await scope.ServiceProvider.GetRequiredService<PlexRequestsHosted.Services.Implementations.IFulfillmentTransferService>()
            .CorrectMisclassifiedMissingAsync();
    }
    catch (Exception ex)
    {
        app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("QualityProfileSeeder")
            .LogError(ex, "Startup data seeding failed; it will be retried on the next start");
    }
}

// Admin-only: write a consistent point-in-time SQLite backup (VACUUM INTO) and stream it to the browser.
// Cookie-authenticated (the button in Admin → System → Database links here), not part of the worker API.
app.MapGet("/api/admin/db/backup", async (PlexRequestsHosted.Services.Abstractions.IDatabaseAdminService dbAdmin) =>
{
    var path = await dbAdmin.CreateBackupAsync();
    return Results.File(path, "application/octet-stream", Path.GetFileName(path));
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/admin/catalog/stats", async (
    PlexRequestsHosted.Services.Abstractions.IReleaseCatalogService catalog,
    CancellationToken cancellationToken) =>
    Results.Ok(await catalog.GetStatsAsync(cancellationToken)))
    .RequireAuthorization("AdminOnly");

app.MapGet("/api/admin/browser-capture/firefox-extension", (IWebHostEnvironment environment) =>
    Results.File(
        PlexRequestsHosted.Services.Implementations.FirefoxExtensionArchive.Create(environment.ContentRootPath),
        "application/x-xpinstall",
        "plexrequests-firefox-capture.xpi"))
    .RequireAuthorization("AdminOnly");

// Firefox capture uses a two-stage credential: an admin creates a short-lived one-time pairing code in
// the Blazor circuit, then the extension exchanges it for a revocable device token. Neither route accepts
// or returns the Firefox session cookies that got through the upstream challenge.
static string? CaptureBearerToken(HttpContext context)
{
    var value = context.Request.Headers.Authorization.ToString();
    return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? value[7..].Trim()
        : null;
}

app.MapPost("/api/browser-capture/pair", async (
    FirefoxCapturePairRequestDto body,
    PlexRequestsHosted.Services.Abstractions.IFirefoxCaptureService capture,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    try
    {
        var result = await capture.RedeemPairingAsync(body, cancellationToken);
        return result is null ? Results.Unauthorized() : Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
})
    .AllowAnonymous()
    .DisableAntiforgery()
    .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
    .RequireRateLimiting("firefox-capture-pair");

app.MapGet("/api/browser-capture/status", async (
    PlexRequestsHosted.Services.Abstractions.IFirefoxCaptureService capture,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var token = CaptureBearerToken(context);
    if (token is null) return Results.Unauthorized();
    var result = await capture.GetConnectionAsync(token, cancellationToken);
    return result is null ? Results.Unauthorized() : Results.Ok(result);
})
    .AllowAnonymous()
    .DisableAntiforgery()
    .RequireRateLimiting("firefox-capture-ingest");

app.MapPost("/api/browser-capture/batches", async (
    FirefoxCaptureBatchDto body,
    PlexRequestsHosted.Services.Abstractions.IFirefoxCaptureService capture,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var token = CaptureBearerToken(context);
    if (token is null) return Results.Unauthorized();
    try
    {
        return Results.Ok(await capture.IngestAsync(token, body, cancellationToken));
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Unauthorized();
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
    .AllowAnonymous()
    .DisableAntiforgery()
    .WithMetadata(new RequestSizeLimitAttribute(512 * 1024))
    .RequireRateLimiting("firefox-capture-ingest");

// Simple health endpoint for Plex connectivity
app.MapGet("/api/plex/health", async (IPlexApiService plex) =>
{
    var info = await plex.GetServerInfoAsync();
    return Results.Ok(new { online = info?.IsOnline == true, name = info?.Name, version = info?.Version });
}).RequireAuthorization();

// Admin-only same-origin artwork proxy: keeps the Plex token/private URL out of rendered image tags.
app.MapGet("/api/plex/artwork", async (
    [FromQuery] string source,
    [FromQuery] int? width,
    [FromQuery] int? height,
    HttpContext context,
    IPlexArtworkService artwork,
    CancellationToken cancellationToken) =>
{
    var image = await artwork.GetAsync(source, width ?? 320, height ?? 480, cancellationToken);
    if (image is null) return Results.NotFound();
    context.Response.Headers.CacheControl = "private,max-age=86400";
    return Results.File(image.Bytes, image.ContentType);
}).RequireAuthorization("AdminOnly");

// Diagnostics: index stats
app.MapGet("/api/plex/index/stats", async (IPlexApiService plex) =>
{
    var stats = await plex.GetIndexStatsAsync();
    return Results.Ok(stats);
}).RequireAuthorization();

// Force rebuild of Plex availability index (dev/diagnostics)
app.MapPost("/api/plex/index/rebuild", async (IPlexApiService plex) =>
{
    var res = await plex.RebuildAvailabilityIndexAsync();
    return Results.Ok(res);
}).RequireAuthorization("AdminOnly");

// Admin folder browser (Library Organization page): lists one directory level at a time so an admin
// can pick a library/NAS path instead of typing it by hand. Read-only, admin-only, cross-platform
// (drive list on Windows, "/" root on Linux/Mac). The Blazor page itself calls IFolderBrowserService
// directly (same process, no need to round-trip through HTTP) — this endpoint exists for parity/any
// external tooling that might want it.
app.MapGet("/api/admin/browse-folders", (string? path, PlexRequestsHosted.Services.Implementations.IFolderBrowserService browser) =>
    Results.Ok(browser.Browse(path)))
    .RequireAuthorization("AdminOnly");

// Diagnostics: test a single match
app.MapGet("/api/plex/match", async (string? title, int? year, int? tmdbId, string? imdbId, int? tvdbId, MediaType mediaType, IPlexApiService plex) =>
{
    var result = await plex.TestMatchAsync(title, year, tmdbId, imdbId, tvdbId, mediaType);
    return Results.Ok(result);
}).RequireAuthorization();

// Low-level helpers for first-success diagnostics
app.MapGet("/api/plex/sections/raw", async (IPlexApiService plex) =>
{
    var raw = await plex.GetSectionsRawAsync();
    return Results.Text(raw, "text/plain");
}).RequireAuthorization();

app.MapGet("/api/plex/metadata/{ratingKey}", async (string ratingKey, IPlexApiService plex) =>
{
    var md = await plex.GetMetadataAsync(ratingKey);
    return Results.Ok(md);
}).RequireAuthorization();

app.MapGet("/api/plex/search", async (string query, MediaType? mediaType, IPlexApiService plex) =>
{
    var results = await plex.SearchServerAsync(query, mediaType);
    return Results.Ok(results);
}).RequireAuthorization();

// ---------------------------------------------------------------------------------------------
// Fulfillment worker API. These endpoints are called by the out-of-process downloader (not a
// browser), so they are NOT cookie-authenticated — they are gated by a shared secret in the
// `X-Fulfillment-Key` header, compared in constant time. Configure Fulfillment:ApiKey (env
// FULFILLMENT_API_KEY) to enable them; with no key configured every call is rejected.
// ---------------------------------------------------------------------------------------------
static bool IsAuthorizedWorker(HttpContext ctx, IConfiguration cfg)
{
    var configured = cfg["Fulfillment:ApiKey"];
    if (string.IsNullOrWhiteSpace(configured)) return false;
    if (!ctx.Request.Headers.TryGetValue("X-Fulfillment-Key", out var provided)) return false;
    var a = Encoding.UTF8.GetBytes(provided.ToString());
    var b = Encoding.UTF8.GetBytes(configured);
    return a.Length == b.Length &&
           System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
}

static string NormalizeLocalReturnUrl(string? returnUrl)
{
    if (string.IsNullOrWhiteSpace(returnUrl)) return "/browse";
    if (!returnUrl.StartsWith('/') || returnUrl.StartsWith("//")) return "/browse";
    if (returnUrl == "/" || returnUrl.StartsWith("/login", StringComparison.OrdinalIgnoreCase)) return "/browse";
    return returnUrl;
}

static PlexRequestsHosted.Shared.DTOs.MediaRequestDto ToRequestDto(PlexRequestsHosted.Infrastructure.Entities.MediaRequestEntity r) => new()
{
    Id = r.Id,
    MediaId = r.MediaId,
    MediaType = r.MediaType,
    Title = r.Title,
    PosterUrl = r.PosterUrl,
    Status = r.Status,
    RequestedAt = r.RequestedAt,
    ApprovedAt = r.ApprovedAt,
    AvailableAt = r.AvailableAt,
    RequestedByUserId = r.RequestedByUserId ?? 0,
    RequestedByUsername = r.RequestedBy ?? string.Empty,
    DenialReason = r.DenialReason,
    ExternalId = r.ExternalId,
    ExternalSource = r.ExternalSource,
    RequestScopeKind = r.RequestScopeKind,
    MediaRef = !string.IsNullOrWhiteSpace(r.ExternalId)
        ? MediaRef.FromExternal(r.ExternalSource ?? "external", r.ExternalId, r.MediaType,
            r.RequestScopeKind == RequestScopeKind.ArtistCatalog ? MediaKind.Artist
            : r.RequestScopeKind == RequestScopeKind.Track ? MediaKind.Track
            : r.MediaType.DefaultKind())
        : MediaRef.FromTmdb(r.MediaId, r.MediaType)
};

// Worker claims queued jobs to download.
app.MapPost("/api/fulfillment/claim", async (ClaimRequest body, HttpContext ctx, IConfiguration cfg, IFulfillmentQueue queue) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    var jobs = await queue.ClaimNextAsync(body.WorkerId ?? "worker", Math.Clamp(body.Max ?? 1, 1, 25));
    return Results.Ok(jobs);
});

// Worker fetches the global download-selection preferences (season-pack strategy, thresholds, etc.).
app.MapGet("/api/fulfillment/config", async (HttpContext ctx, IConfiguration cfg, PlexRequestsHosted.Services.Implementations.IDownloadPreferencesService prefs) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    return Results.Ok(await prefs.GetAsync());
});

// Worker fetches the per-indexer enable/priority config (the admin Indexers panel) — hot-reloadable,
// same pattern as /config above. Providers the panel doesn't know yet default to enabled downloader-side.
app.MapGet("/api/fulfillment/indexers", async (HttpContext ctx, IConfiguration cfg, PlexRequestsHosted.Services.Abstractions.IIndexerAdminService indexers) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    return Results.Ok(await indexers.GetWorkerConfigAsync());
});

// Worker reports each indexer's outcome for a search pass (result count / error / latency), which the
// admin Indexers panel surfaces as per-provider health. Unknown provider names auto-register.
app.MapPost("/api/fulfillment/indexer-status", async (List<PlexRequestsHosted.Shared.DTOs.IndexerStatusReportDto> body, HttpContext ctx, IConfiguration cfg, PlexRequestsHosted.Services.Abstractions.IIndexerAdminService indexers) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    await indexers.ReportStatusAsync(body);
    return Results.Ok();
});

// ---- Interactive search -------------------------------------------------------------------------
// The worker polls for admin-initiated searches. This is a pull, not a push, because the web app has no
// route to the downloader: it has no listening port and, under the VPN compose file, shares gluetun's
// network namespace. A short poll interval is what keeps the admin-perceived latency to a couple of seconds.
// ---- Torrent reconciliation -------------------------------------------------------------------
// The downloader registers what it added, then reconciles it against the download client on every pass.
// This is a pull-and-push loop over durable state rather than in-process monitoring, so it is indifferent
// to either side restarting: the next cycle re-derives everything from the client plus the database.
app.MapPost("/api/fulfillment/{jobId:int}/transfers", async (int jobId, List<PlexRequestsHosted.Shared.DTOs.TrackedTransferDto> body, HttpContext ctx, IConfiguration cfg, PlexRequestsHosted.Services.Implementations.IFulfillmentTransferService svc) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    return Results.Ok(await svc.RegisterAsync(jobId, body));
});

// Release catalog ingestion uses at-least-once batches. The catalog service advances the opaque source
// cursor in the same transaction as its releases and receipt, so a worker crash can safely replay.
app.MapPost("/api/fulfillment/catalog/batches", async (
    CatalogBatchDto body,
    HttpContext ctx,
    IConfiguration cfg,
    Microsoft.Extensions.Options.IOptions<PlexRequestsHosted.Infrastructure.Catalog.CatalogOptions> options,
    PlexRequestsHosted.Services.Abstractions.IReleaseCatalogService catalog,
    CancellationToken cancellationToken) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    if (!options.Value.Enabled) return Results.NotFound();
    try { return Results.Ok(await catalog.UpsertBatchAsync(body, cancellationToken)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/fulfillment/catalog/checkpoints/{indexerId:int}", async (
    int indexerId,
    HttpContext ctx,
    IConfiguration cfg,
    Microsoft.Extensions.Options.IOptions<PlexRequestsHosted.Infrastructure.Catalog.CatalogOptions> options,
    PlexRequestsHosted.Services.Abstractions.IReleaseCatalogService catalog,
    CancellationToken cancellationToken) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    if (!options.Value.Enabled) return Results.NotFound();
    return Results.Ok(await catalog.GetCheckpointAsync(indexerId, cancellationToken));
});

app.MapPost("/api/fulfillment/catalog/failures", async (
    CatalogFailureDto body,
    HttpContext ctx,
    IConfiguration cfg,
    Microsoft.Extensions.Options.IOptions<PlexRequestsHosted.Infrastructure.Catalog.CatalogOptions> options,
    PlexRequestsHosted.Services.Abstractions.IReleaseCatalogService catalog,
    CancellationToken cancellationToken) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    if (!options.Value.Enabled) return Results.NotFound();
    try { return Results.Ok(await catalog.ReportFailureAsync(body, cancellationToken)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/fulfillment/catalog/search", async (
    CatalogQueryDto body,
    HttpContext ctx,
    IConfiguration cfg,
    Microsoft.Extensions.Options.IOptions<PlexRequestsHosted.Infrastructure.Catalog.CatalogOptions> options,
    PlexRequestsHosted.Services.Abstractions.IReleaseCatalogService catalog,
    CancellationToken cancellationToken) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    if (!options.Value.Enabled) return Results.NotFound();
    return Results.Ok(await catalog.SearchAsync(body, cancellationToken));
});

app.MapGet("/api/fulfillment/jobs/{jobId:int}", async (int jobId, HttpContext ctx, IConfiguration cfg, IFulfillmentQueue queue) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    var job = await queue.GetJobAsync(jobId);
    return job is null ? Results.NotFound() : Results.Ok(job);
});

app.MapGet("/api/fulfillment/transfers/active", async (HttpContext ctx, IConfiguration cfg, PlexRequestsHosted.Services.Implementations.IFulfillmentTransferService svc) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    return Results.Ok(await svc.GetActiveAsync());
});

app.MapPost("/api/fulfillment/transfers/state", async (List<PlexRequestsHosted.Shared.DTOs.TransferStateUpdateDto> body, HttpContext ctx, IConfiguration cfg, PlexRequestsHosted.Services.Implementations.IFulfillmentTransferService svc) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    return Results.Ok(await svc.ApplyAsync(body));
});

app.MapPost("/api/fulfillment/search/claim", async (ClaimRequest body, HttpContext ctx, IConfiguration cfg, PlexRequestsHosted.Services.Implementations.IInteractiveSearchService search) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    var task = await search.ClaimAsync(body.WorkerId ?? "worker");
    return task is null ? Results.NoContent() : Results.Ok(task);
});

app.MapPost("/api/fulfillment/search/{taskId:int}/results", async (int taskId, PlexRequestsHosted.Shared.DTOs.SearchTaskResultDto body, HttpContext ctx, IConfiguration cfg, PlexRequestsHosted.Services.Implementations.IInteractiveSearchService search) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    return await search.CompleteAsync(taskId, body) ? Results.Ok() : Results.NotFound();
});

// The worker reports a release that failed, so it is never grabbed for this request again. Called from the
// pipeline's single failure funnel, so every failure path (stall, error, unresolvable path, failed import)
// records one.
app.MapPost("/api/fulfillment/{jobId:int}/blocklist", async (int jobId, PlexRequestsHosted.Shared.DTOs.BlocklistRequestDto body, HttpContext ctx, IConfiguration cfg, PlexRequestsHosted.Services.Implementations.IReleaseBlocklistService blocklist) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    return await blocklist.BlockAsync(jobId, body) ? Results.Ok() : Results.NotFound();
});

// Episodes automatic release monitoring should watch for: monitored, aired (or undated), and not yet on Plex.
app.MapGet("/api/fulfillment/wanted", async (HttpContext ctx, IConfiguration cfg, AppDbContext db) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    var now = DateTime.UtcNow;
    var wanted = await (from a in db.AirSchedule
                        join r in db.MediaRequests on a.MediaRequestId equals r.Id
                        where a.Monitored && !a.HasFile && a.MediaRequestId != null
                              && (a.SearchState == PlexRequestsHosted.Shared.Enums.AirSearchState.Due
                                  || a.SearchState == PlexRequestsHosted.Shared.Enums.AirSearchState.Searching
                                  || a.SearchState == PlexRequestsHosted.Shared.Enums.AirSearchState.AirDateUnknown)
                              && (a.AirsAtUtc == null || a.AirsAtUtc <= now)
                        select new PlexRequestsHosted.Shared.DTOs.WantedEpisodeDto
                        {
                            MediaRequestId = a.MediaRequestId!.Value,
                            ShowTmdbId = a.ShowTmdbId,
                            Title = r.Title,
                            ImdbId = db.FulfillmentJobs
                                .Where(j => j.MediaRequestId == r.Id && j.ImdbId != null)
                                .OrderByDescending(j => j.Id)
                                .Select(j => j.ImdbId)
                                .FirstOrDefault(),
                            IsAnime = db.FulfillmentJobs.Any(j => j.MediaRequestId == r.Id && j.IsAnime),
                            Season = a.SeasonNumber,
                            Episode = a.EpisodeNumber,
                            QualityProfileId = r.QualityProfileId
                        }).Take(500).ToListAsync();
    return Results.Ok(wanted);
});

// The sweep found a wanted episode. Route it through the normal monitored-episode path so it gets the same
// ranking, blocklist and import treatment as anything else — the sweep only changes WHEN we notice.
app.MapPost("/api/fulfillment/rss-grab", async (List<PlexRequestsHosted.Shared.DTOs.RssGrabDto> body, HttpContext ctx, IConfiguration cfg, AppDbContext db, IMediaRequestService requests) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    int queued = 0;
    var now = DateTime.UtcNow;
    foreach (var group in body.GroupBy(g => g.MediaRequestId))
    {
        var episodes = group.Select(g => (g.Season, g.Episode)).Distinct().ToList();
        var result = await requests.CreateMonitoredEpisodesAsync(group.Key, episodes);
        foreach (var g in group)
        {
            var row = await db.AirSchedule.FirstOrDefaultAsync(a =>
                a.MediaRequestId == g.MediaRequestId && a.SeasonNumber == g.Season && a.EpisodeNumber == g.Episode);
            if (row is null) continue;
            if (!result.AlreadyCovered)
            {
                row.LastSearchedAt = now;
                row.SearchAttempts++;
            }

            if (result.Success)
            {
                row.SearchState = PlexRequestsHosted.Shared.Enums.AirSearchState.Searching;
                row.NextSearchAt = now.AddMinutes(30);
            }
            else
            {
                row.SearchState = PlexRequestsHosted.Shared.Enums.AirSearchState.Due;
                row.NextSearchAt = PlexRequestsHosted.Services.Jobs.RetryBackoff.ComputeNextRetry(
                    Math.Max(0, row.SearchAttempts - 1), releaseDate: null, now);
            }
        }

        if (result.Success && result.JobQueued) queued += episodes.Count;
    }
    await db.SaveChangesAsync();
    return Results.Ok(queued);
});

// The worker pushes the titles it saw trending. It browses rather than the web app doing so because the
// web container is outside the VPN tunnel — scraping from there would egress on the user's real IP.
app.MapPost("/api/fulfillment/recommended", async (List<PlexRequestsHosted.Shared.DTOs.RecommendedFeedItemDto> body, HttpContext ctx, IConfiguration cfg, PlexRequestsHosted.Services.Implementations.IRecommendedFeedService feed) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    return Results.Ok(await feed.IngestAsync(body));
});

// The worker pushes any Torznab endpoints configured in ITS environment so they become admin-managed rows.
// This direction is forced: those variables live in the downloader's container, which the web app can't
// read, and the downloader is the only side that can initiate a connection anyway (it sits behind the VPN).
// Idempotent by URL, so a restart or a lost marker file can't duplicate rows.
app.MapPost("/api/fulfillment/indexers/import-legacy", async (List<PlexRequestsHosted.Shared.DTOs.LegacyTorznabEndpointDto> body, HttpContext ctx, IConfiguration cfg, PlexRequestsHosted.Services.Abstractions.IIndexerAdminService indexers) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    var imported = await indexers.ImportLegacyTorznabAsync(body);
    return Results.Ok(imported);
});

// Worker fetches the admin-configured library organization settings (paths, naming templates, transfer
// mode, archive/subtitle/season-pack-split toggles) — hot-reloadable, same pattern as /config above.
app.MapGet("/api/fulfillment/library-config", async (HttpContext ctx, IConfiguration cfg, PlexRequestsHosted.Services.Implementations.ILibraryOrganizationPreferencesService libraryPrefs) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    return Results.Ok(await libraryPrefs.GetAsync());
});

// Worker fetches the admin-configured network shares WITH decrypted credentials, so it can mount them
// read-write and place files there. Credentials cross only the internal Docker network and only to a
// caller holding the shared fulfillment secret — the same trust boundary as every endpoint here.
app.MapGet("/api/fulfillment/network-shares", async (HttpContext ctx, IConfiguration cfg, PlexRequestsHosted.Services.Implementations.INetworkShareService shares) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    return Results.Ok(await shares.GetMountConfigsAsync());
});

// Worker fetches cached TMDB episode titles for a season, to name individual files in a season pack.
// Fetched lazily at import time (not bundled at enqueue) so titles stay fresh and cover every episode
// in the pack, not just the ones that were missing from Plex when the job was originally enqueued.
app.MapGet("/api/fulfillment/episodes", async (int tmdbId, int season, HttpContext ctx, IConfiguration cfg, PlexRequestsHosted.Services.Abstractions.IMediaMetadataProvider metadata) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    var episodes = await metadata.GetSeasonEpisodesAsync(tmdbId, season);
    return Results.Ok(episodes);
});

// Worker persists the durable audit trail of what got imported for a job (one row per video/subtitle
// file) — otherwise the only record is a transient JSON file the worker deletes once the job completes.
// Both downloader observers can report the same coordinated import at nearly the same instant. Serialize
// this tiny write boundary so the read-before-insert dedupe remains atomic within the web process.
var importedFilesWriteLock = new SemaphoreSlim(1, 1);
app.MapPost("/api/fulfillment/{jobId:int}/imported-files", async (int jobId, List<PlexRequestsHosted.Shared.DTOs.ImportedFileDto> files, HttpContext ctx, IConfiguration cfg, AppDbContext db) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    if (!await db.FulfillmentJobs.AnyAsync(j => j.Id == jobId)) return Results.NotFound();

    await importedFilesWriteLock.WaitAsync(ctx.RequestAborted);
    try
    {
        static string AuditKey(AcquisitionProtocol protocol, string? transferId, string destinationPath, string fileType) =>
            $"{(int)protocol}\u001f{transferId?.Trim()}\u001f{destinationPath.Trim()}\u001f{fileType.Trim()}";

        var existing = await db.ImportedFiles.AsNoTracking()
            .Where(f => f.FulfillmentJobId == jobId)
            .Select(f => new { f.Protocol, f.TransferId, f.DestinationPath, f.FileType })
            .ToListAsync(ctx.RequestAborted);
        var known = existing.Select(f => AuditKey(f.Protocol, f.TransferId, f.DestinationPath, f.FileType))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var f in files)
        {
            if (!known.Add(AuditKey(f.Protocol, f.TransferId, f.DestinationPath, f.FileType))) continue;
            db.ImportedFiles.Add(new PlexRequestsHosted.Infrastructure.Entities.ImportedFileEntity
            {
                FulfillmentJobId = jobId,
                TransferId = f.TransferId,
                Protocol = f.Protocol,
                SourcePath = f.SourcePath,
                DestinationPath = f.DestinationPath,
                FileType = f.FileType,
                SeasonNumber = f.SeasonNumber,
                EpisodeNumber = f.EpisodeNumber,
                SizeBytes = f.SizeBytes,
                ResolutionHeight = f.ResolutionHeight,
                ReleaseName = string.IsNullOrWhiteSpace(f.ReleaseName) ? null
                    : f.ReleaseName.Length > 512 ? f.ReleaseName[..512] : f.ReleaseName,
                InfoHash = f.Protocol == AcquisitionProtocol.Torrent
                    ? PlexRequestsHosted.Shared.Releases.MagnetUtil.Normalize(f.SourceId)
                    : null
            });
            added++;
        }

        if (added > 0) await db.SaveChangesAsync(ctx.RequestAborted);
        return Results.Ok(new { added });
    }
    finally
    {
        importedFilesWriteLock.Release();
    }
});

// Worker asks Plex to rescan the relevant library section after a successful import — previously nothing
// ever called this at all, so Plex relied entirely on its own periodic scan to notice new files.
app.MapPost("/api/fulfillment/refresh-library", async (RefreshLibraryRequest body, HttpContext ctx, IConfiguration cfg, IPlexApiService plex) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    var sectionKey = await plex.ResolveSectionKeyAsync(body.MediaType);
    if (sectionKey is null) return Results.Ok(new { refreshed = false, reason = "No matching Plex library section" });
    try
    {
        await plex.RefreshLibraryAsync(sectionKey);
        return Results.Ok(new { refreshed = true });
    }
    catch (Exception ex)
    {
        // Best-effort — a refresh failure should never fail the worker's import flow.
        return Results.Ok(new { refreshed = false, reason = ex.Message });
    }
});

app.MapPost("/api/fulfillment/verify-library", async (PlexVerificationRequest body, HttpContext ctx,
    IConfiguration cfg, PlexRequestsHosted.Services.Implementations.IPlexMusicService music) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    if (body.MediaType != MediaType.Music)
        return Results.Ok(new PlexVerificationResult(true, Detail: "Video verification remains handled by the availability index"));
    return Results.Ok(await music.VerifyAsync(body, ctx.RequestAborted));
});

// Worker reports download progress; reflects the request as Processing for the UI.
app.MapPost("/api/fulfillment/{jobId:int}/progress", async (int jobId, ProgressRequest body, HttpContext ctx, IConfiguration cfg, IFulfillmentQueue queue, AppDbContext db, PlexRequestsHosted.Services.Abstractions.IDownloadTelemetryStore telemetry) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    var ok = await queue.ReportProgressAsync(jobId, body.Progress);
    if (!ok) return Results.NotFound();
    // Stash the live per-torrent snapshot for the admin downloads panel (ephemeral; not persisted).
    if (body.Transfers is not null) telemetry.Update(jobId, body.Transfers);
    var job = await db.FulfillmentJobs.FirstOrDefaultAsync(j => j.Id == jobId);
    if (job is not null)
    {
        var req = await db.MediaRequests.FirstOrDefaultAsync(r => r.Id == job.MediaRequestId);
        if (req is not null && req.Status == RequestStatus.Approved)
        {
            req.Status = RequestStatus.Processing;
            await db.SaveChangesAsync();
        }
    }
    return Results.Ok();
});

// Worker reports success -> mark Available, close the job, rebuild the Plex index, notify requester.
app.MapPost("/api/requests/{id:int}/fulfilled", async (int id, HttpContext ctx, IConfiguration cfg, AppDbContext db, IFulfillmentQueue queue, PlexRequestsHosted.Services.Abstractions.INotificationService notify, IPlexApiService plex) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    var req = await db.MediaRequests.FirstOrDefaultAsync(r => r.Id == id);
    if (req is null) return Results.NotFound();

    var alreadyAvailable = req.Status == RequestStatus.Available;
    if (!alreadyAvailable)
    {
        req.Status = RequestStatus.Available;
        req.AvailableAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
    await queue.MarkCompletedAsync(id);
    // Record the quality we actually got (min video resolution across imported files) and flag the request
    // for a later upgrade if it's below the preferred target.
    try { await queue.RecomputeAchievedQualityAsync(id); } catch { /* best-effort; never block fulfillment */ }

    if (!alreadyAvailable)
    {
        try { await plex.RebuildAvailabilityIndexAsync(); } catch { /* index refresh is best-effort */ }
        await notify.RequestAvailableAsync(ToRequestDto(req));
    }
    return Results.Ok(new { req.Id, status = req.Status.ToString() });
});

// Worker reports unrecoverable failure -> mark Failed (needs admin attention), close the job, notify admins.
app.MapPost("/api/requests/{id:int}/failed", async (int id, FailRequest body, HttpContext ctx, IConfiguration cfg, AppDbContext db, IFulfillmentQueue queue, PlexRequestsHosted.Services.Abstractions.INotificationService notify) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    var req = await db.MediaRequests.FirstOrDefaultAsync(r => r.Id == id);
    if (req is null) return Results.NotFound();

    var reason = string.IsNullOrWhiteSpace(body.Reason) ? "Fulfillment failed" : body.Reason!;
    req.Status = RequestStatus.Failed;
    req.DenialReason = reason.Length > 1000 ? reason[..1000] : reason;
    await db.SaveChangesAsync();
    await queue.MarkFailedAsync(id, reason);
    await notify.RequestFailedAsync(ToRequestDto(req), reason);
    return Results.Ok(new { req.Id, status = req.Status.ToString() });
});

// Worker reports a partial success: some seasons/episodes imported before another torrent in the same
// job failed. Distinct from a hard failure so a retry can target only what's still missing, and the UI
// can say "partially available" instead of implying nothing arrived.
app.MapPost("/api/requests/{id:int}/partially-completed", async (int id, FailRequest body, HttpContext ctx, IConfiguration cfg, AppDbContext db, IFulfillmentQueue queue, PlexRequestsHosted.Services.Abstractions.INotificationService notify, IPlexApiService plex) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    var req = await db.MediaRequests.FirstOrDefaultAsync(r => r.Id == id);
    if (req is null) return Results.NotFound();

    var reason = string.IsNullOrWhiteSpace(body.Reason) ? "Some content imported before the rest failed" : body.Reason!;
    req.Status = RequestStatus.PartiallyAvailable;
    req.DenialReason = reason.Length > 1000 ? reason[..1000] : reason;
    req.AvailableAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    await queue.MarkPartiallyCompletedAsync(id, reason);
    try { await plex.RebuildAvailabilityIndexAsync(); } catch { /* index refresh is best-effort */ }
    await notify.RequestPartiallyAvailableAsync(ToRequestDto(req), reason);
    return Results.Ok(new { req.Id, status = req.Status.ToString() });
});

// Worker reports "no release findable yet" for a NON-upgrade job — the never-dead-end path. The job is
// parked (Deferred) on a growing backoff and the request shows as "Searching" (not Failed); the scheduler
// re-queues it when the backoff elapses. After enough empty searches, admins are notified once — but the
// request keeps retrying and is never auto-failed.
app.MapPost("/api/fulfillment/{jobId:int}/deferred", async (int jobId, FailRequest body, HttpContext ctx, IConfiguration cfg, AppDbContext db, IFulfillmentQueue queue, PlexRequestsHosted.Services.Abstractions.INotificationService notify) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    var reason = string.IsNullOrWhiteSpace(body.Reason) ? "No release found yet" : body.Reason!;
    var result = await queue.MarkDeferredAsync(jobId, reason, body.CandidatesRejected);
    if (!result.Found) return Results.NotFound();

    var job = await db.FulfillmentJobs.FirstOrDefaultAsync(j => j.Id == jobId);
    if (job is not null)
    {
        var req = await db.MediaRequests.FirstOrDefaultAsync(r => r.Id == job.MediaRequestId);
        if (req is not null)
        {
            // Reflect it as Searching (a soft, non-failure state) if it's still in an active pre-download state.
            if (req.Status is RequestStatus.Approved or RequestStatus.Processing or RequestStatus.Pending or RequestStatus.Searching)
            {
                if (req.Status != RequestStatus.Searching) { req.Status = RequestStatus.Searching; await db.SaveChangesAsync(); }
            }
            if (result.ShouldEscalate)
                await notify.RequestSearchStalledAsync(ToRequestDto(req), result.DeferCount);
        }
    }
    return Results.Ok(new { jobId, deferCount = result.DeferCount, nextRetryAt = result.NextRetryAt });
});

// Worker reports an upgrade search found nothing better. The content is already available; close the upgrade
// job quietly (not a failure) and let the request be reconsidered on a later scan.
app.MapPost("/api/fulfillment/{jobId:int}/upgrade-exhausted", async (int jobId, HttpContext ctx, IConfiguration cfg, IFulfillmentQueue queue) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    await queue.MarkUpgradeExhaustedAsync(jobId);
    return Results.Ok();
});

// Worker reports a successful quality upgrade: the better release imported and the old files were deleted on
// disk. Drop the superseded audit rows, recompute the request's achieved quality, refresh Plex, and notify.
app.MapPost("/api/fulfillment/{jobId:int}/upgraded", async (int jobId, HttpContext ctx, IConfiguration cfg, AppDbContext db, IFulfillmentQueue queue, PlexRequestsHosted.Services.Abstractions.INotificationService notify, IPlexApiService plex) =>
{
    if (!IsAuthorizedWorker(ctx, cfg)) return Results.Unauthorized();
    var job = await db.FulfillmentJobs.FirstOrDefaultAsync(j => j.Id == jobId);
    if (job is null) return Results.NotFound();

    // Supersede the OLD audit rows for exactly the content this upgrade re-imported, matched by
    // (season, episode) so a PARTIAL upgrade only replaces the episodes it actually got — a failed episode
    // keeps its original row (and file). A movie (no season/episode) replaces the whole title.
    var newFiles = await db.ImportedFiles.Where(f => f.FulfillmentJobId == jobId).ToListAsync();
    var requestJobIds = await db.FulfillmentJobs.Where(j => j.MediaRequestId == job.MediaRequestId)
        .Select(j => j.Id).ToListAsync();
    var oldRows = await db.ImportedFiles
        .Where(f => requestJobIds.Contains(f.FulfillmentJobId) && f.FulfillmentJobId != jobId)
        .ToListAsync();

    List<PlexRequestsHosted.Infrastructure.Entities.ImportedFileEntity> superseded;
    if (newFiles.Any(f => f.SeasonNumber == null && f.EpisodeNumber == null))
    {
        // Movie / whole-title upgrade — every prior file for the request is replaced.
        superseded = oldRows;
    }
    else
    {
        var upgradedEps = newFiles.Where(f => f.SeasonNumber != null && f.EpisodeNumber != null)
            .Select(f => (f.SeasonNumber, f.EpisodeNumber)).ToHashSet();
        superseded = oldRows.Where(f => upgradedEps.Contains((f.SeasonNumber, f.EpisodeNumber))).ToList();
    }
    if (superseded.Count > 0) { db.ImportedFiles.RemoveRange(superseded); await db.SaveChangesAsync(); }

    job.Status = FulfillmentStatus.Completed;
    job.Progress = 100;
    job.CompletedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();

    var newQuality = await queue.RecomputeAchievedQualityAsync(job.MediaRequestId);
    var req = await db.MediaRequests.FirstOrDefaultAsync(r => r.Id == job.MediaRequestId);
    if (req is not null)
    {
        try { await plex.RebuildAvailabilityIndexAsync(); } catch { /* best-effort */ }
        if (job.IsReplacement)
        {
            if (job.MediaIssueId is int issueId)
            {
                var issue = await db.MediaIssues.FirstOrDefaultAsync(i => i.Id == issueId);
                if (issue is not null)
                {
                    issue.Status = IssueStatus.Resolved;
                    issue.ResolvedAt = DateTime.UtcNow;
                    issue.ResolvedBy = "Automatic replacement";
                    await db.SaveChangesAsync();
                }
            }
            var target = job.RequestedEpisodesCsv?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "title";
            await notify.RequestReplacedAsync(ToRequestDto(req), target);
        }
        else
        {
            await notify.RequestUpgradedAsync(ToRequestDto(req), newQuality);
        }
    }
    return Results.Ok(new { jobId, achievedQuality = newQuality.ToString() });
});

// ---------------------------------------------------------------------------------------------
// Discord bridge API. Called by the PlexRequestsBridge extension in PlexBot (not a browser), so it
// is gated by a shared secret in the `X-Bridge-Key` header (constant-time compare). Configure
// Bridge:ApiKey (env BRIDGE_API_KEY) + Bridge:Enabled to turn it on.
// ---------------------------------------------------------------------------------------------
static bool IsAuthorizedBridge(HttpContext ctx, IConfiguration cfg)
{
    if (!cfg.GetValue<bool>("Bridge:Enabled")) return false;
    var configured = cfg["Bridge:ApiKey"];
    if (string.IsNullOrWhiteSpace(configured)) return false;
    if (!ctx.Request.Headers.TryGetValue("X-Bridge-Key", out var provided)) return false;
    var a = Encoding.UTF8.GetBytes(provided.ToString());
    var b = Encoding.UTF8.GetBytes(configured);
    return a.Length == b.Length &&
           System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
}

app.MapGet("/api/bridge/capabilities", async (HttpContext ctx, IConfiguration cfg,
    IMediaModuleRegistry modules, IMusicSettingsService musicSettings, IBridgeOutboxService outbox) =>
{
    if (!IsAuthorizedBridge(ctx, cfg)) return Results.Unauthorized();
    var music = await musicSettings.GetAsync();
    var descriptors = modules.All.Select(module =>
    {
        if (module.MediaType != MediaType.Music) return module;
        var capabilities = module.Capabilities;
        if (!music.CatalogEnabled)
            capabilities &= ~(MediaModuleCapabilities.Search | MediaModuleCapabilities.Discovery);
        if (!music.RequestsEnabled)
            capabilities &= ~(MediaModuleCapabilities.Request | MediaModuleCapabilities.AutomaticFulfillment
                              | MediaModuleCapabilities.InteractiveSearch);
        return module with
        {
            Enabled = music.CatalogEnabled || music.RequestsEnabled,
            Capabilities = capabilities,
            UnavailableReason = !music.CatalogEnabled && !music.RequestsEnabled
                ? "Music is disabled by the Plex Requests administrator."
                : null
        };
    }).ToList();
    return Results.Ok(new BridgeCapabilitiesDto
    {
        MediaModules = descriptors,
        EventRetentionDays = outbox.RetentionDays
    });
});

// Catalog search (optionally personalized with the caller's request status if their Discord is linked).
app.MapGet("/api/bridge/search", async (string q, string? type, int? limit, string? discordUserId,
    HttpContext ctx, IConfiguration cfg, IMediaMetadataProvider metadata, IPlexApiService plex,
    IDiscordLinkService link, IMediaRequestService requests, IMediaModuleRegistry modules) =>
{
    if (!IsAuthorizedBridge(ctx, cfg)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(q)) return Results.Ok(Array.Empty<BridgeSearchResultDto>());

    var take = Math.Clamp(limit ?? 10, 1, 25);
    MediaType? mt = type?.ToLowerInvariant() switch
    {
        "movie" or "movies" => MediaType.Movie,
        "tv" or "tvshow" or "show" or "series" => MediaType.TvShow,
        "anime" => MediaType.Anime,
        "music" or "album" or "artist" or "track" => MediaType.Music,
        _ => null
    };
    MediaKind? kind = type?.ToLowerInvariant() switch
    {
        "album" => MediaKind.Album,
        "artist" => MediaKind.Artist,
        "track" => MediaKind.Track,
        _ => null
    };
    if (mt is MediaType requestedType && !modules.IsEnabled(requestedType))
        return Results.Ok(Array.Empty<BridgeSearchResultDto>());

    var results = (await metadata.SearchAsync(new MediaSearchQuery
    {
        Query = q.Trim(),
        MediaType = mt,
        Kind = kind,
        Page = 1,
        PageSize = take
    })).Take(take).ToList();
    try { await plex.AnnotateAvailabilityAsync(results); } catch { /* availability best-effort */ }

    // Per-user request status if this Discord user is linked.
    Dictionary<string, RequestStatus> statusMap = new(StringComparer.OrdinalIgnoreCase);
    if (!string.IsNullOrWhiteSpace(discordUserId))
    {
        var uid = await link.ResolveUserIdAsync(discordUserId);
        if (uid is not null)
            foreach (var r in await requests.GetRequestsForUserAsync(uid.Value, 200))
                statusMap[r.MediaRef?.StableKey ?? MediaRef.FromTmdb(r.MediaId, r.MediaType).StableKey] = r.Status;
    }

    var dtos = results.Select(r => new BridgeSearchResultDto
    {
        Media = r.ResolveMediaRef(),
        MediaId = r.Id,
        MediaType = r.MediaType,
        Title = r.Title,
        Year = r.Year,
        Overview = r.Overview,
        PosterUrl = r.PosterUrl,
        BackdropUrl = r.BackdropUrl,
        Rating = r.Rating,
        TmdbId = r.TmdbId,
        Genres = r.Genres,
        AvailableOnPlex = r.IsAvailable,
        RequestStatus = statusMap.TryGetValue(r.ResolveMediaRef().StableKey, out var st) ? st : null
    }).ToList();
    return Results.Ok(dtos);
});

// Create a request on behalf of a linked Discord user.
app.MapPost("/api/bridge/request", async (BridgeRequestBody body, HttpContext ctx, IConfiguration cfg,
    IDiscordLinkService link, IMediaRequestService requests) =>
{
    if (!IsAuthorizedBridge(ctx, cfg)) return Results.Unauthorized();
    var uid = await link.ResolveUserIdAsync(body.DiscordUserId);
    if (uid is null)
        return Results.Ok(new BridgeRequestResultDto { Success = false, NotLinked = true, Message = "Link your account first: run /request link with the code from your Plex Requests profile." });

    var result = await requests.RequestMediaForUserAsync(uid.Value, body.ResolveMedia(), body.Scope,
        body.QualityProfileId);
    return Results.Ok(new BridgeRequestResultDto
    {
        Success = result.Success,
        RequestId = result.RequestId,
        Status = result.NewStatus,
        Message = result.Success ? "Request submitted." : result.ErrorMessage
    });
});

// A linked user's requests (for /request status).
app.MapGet("/api/bridge/requests", async (string discordUserId, int? limit, HttpContext ctx,
    IConfiguration cfg, IDiscordLinkService link, IMediaRequestService requests) =>
{
    if (!IsAuthorizedBridge(ctx, cfg)) return Results.Unauthorized();
    var uid = await link.ResolveUserIdAsync(discordUserId);
    if (uid is null) return Results.Ok(new BridgeUserRequestsDto { Linked = false });

    var take = Math.Clamp(limit ?? 15, 1, 50);
    var list = await requests.GetRequestsForUserAsync(uid.Value, take);
    return Results.Ok(new BridgeUserRequestsDto
    {
        Linked = true,
        Items = list.Select(r => new BridgeUserRequestDto
        {
            RequestId = r.Id, Title = r.Title, MediaType = r.MediaType, Status = r.Status,
            Media = r.MediaRef, RequestScope = r.RequestScopeKind,
            PosterUrl = r.PosterUrl, RequestedAt = r.RequestedAt, DenialReason = r.DenialReason
        }).ToList()
    });
});

// Complete account linking with the one-time code from the web Profile page.
app.MapPost("/api/bridge/link", async (BridgeLinkBody body, HttpContext ctx, IConfiguration cfg, IDiscordLinkService link) =>
{
    if (!IsAuthorizedBridge(ctx, cfg)) return Results.Unauthorized();
    return Results.Ok(await link.CompleteLinkAsync(body.Code, body.DiscordUserId, body.DiscordUsername));
});

app.MapGet("/api/bridge/link/status", async (string discordUserId, HttpContext ctx, IConfiguration cfg, IDiscordLinkService link) =>
{
    if (!IsAuthorizedBridge(ctx, cfg)) return Results.Unauthorized();
    return Results.Ok(await link.GetStatusByDiscordIdAsync(discordUserId));
});

// Admin approve/deny from Discord buttons (verifies the Discord user maps to an admin).
app.MapPost("/api/bridge/requests/{id:int}/approve", async (int id, BridgeAdminActionBody body, HttpContext ctx,
    IConfiguration cfg, IDiscordLinkService link, IMediaRequestService requests) =>
{
    if (!IsAuthorizedBridge(ctx, cfg)) return Results.Unauthorized();
    if (!await link.IsAdminAsync(body.DiscordUserId)) return Results.Json(new { ok = false, message = "You are not an admin." }, statusCode: 403);
    var ok = await requests.ApproveRequestAsAdminAsync(id, body.Reason);
    return Results.Ok(new { ok });
});

app.MapPost("/api/bridge/requests/{id:int}/deny", async (int id, BridgeAdminActionBody body, HttpContext ctx,
    IConfiguration cfg, IDiscordLinkService link, IMediaRequestService requests) =>
{
    if (!IsAuthorizedBridge(ctx, cfg)) return Results.Unauthorized();
    if (!await link.IsAdminAsync(body.DiscordUserId)) return Results.Json(new { ok = false, message = "You are not an admin." }, statusCode: 403);
    var ok = await requests.DenyRequestAsAdminAsync(id, string.IsNullOrWhiteSpace(body.Reason) ? "Denied via Discord" : body.Reason!);
    return Results.Ok(new { ok });
});

// Legacy v1/v2 request-lifecycle event feed. Kept as an array so PlexBox can upgrade independently.
app.MapGet("/api/bridge/events", async (long? since, int? max, HttpContext ctx, IConfiguration cfg,
    IBridgeOutboxService outbox, CancellationToken cancellationToken) =>
{
    if (!IsAuthorizedBridge(ctx, cfg)) return Results.Unauthorized();
    var take = Math.Clamp(max ?? 25, 1, 100);
    var batch = await outbox.ReadBatchAsync(since ?? 0, take, cancellationToken);
    // Old PlexBox builds know only the original five enum values. Preserve their historical rendering
    // semantics here; v3 consumers receive the precise lifecycle types from the endpoint below.
    foreach (var bridgeEvent in batch.Events)
        bridgeEvent.Type = bridgeEvent.Type switch
        {
            BridgeEventType.Searching or BridgeEventType.PartiallyAvailable => BridgeEventType.Failed,
            BridgeEventType.Upgraded => BridgeEventType.Available,
            BridgeEventType.Cancelled => BridgeEventType.Denied,
            _ => bridgeEvent.Type
        };
    return Results.Ok(batch.Events);
});

// Version 3 adds an explicit batch envelope, stable event ids and acknowledgement. The bot persists
// NextCursor only after it has handled the complete batch, then acknowledges that cursor.
app.MapGet("/api/bridge/events/v3", async (long? since, int? max, HttpContext ctx, IConfiguration cfg,
    IBridgeOutboxService outbox, CancellationToken cancellationToken) =>
{
    if (!IsAuthorizedBridge(ctx, cfg)) return Results.Unauthorized();
    return Results.Ok(await outbox.ReadBatchAsync(since ?? 0, Math.Clamp(max ?? 25, 1, 100),
        cancellationToken));
});

app.MapPost("/api/bridge/events/ack", async (BridgeEventAckBody body, HttpContext ctx,
    IConfiguration cfg, IBridgeOutboxService outbox, CancellationToken cancellationToken) =>
{
    if (!IsAuthorizedBridge(ctx, cfg)) return Results.Unauthorized();
    if (body.Cursor < 0) return Results.BadRequest(new { message = "Cursor cannot be negative." });
    return Results.Ok(await outbox.AcknowledgeAsync(body.Cursor, cancellationToken));
});

// Local dev-only login shortcut - bypass Plex auth for UI development.
// Requires ASPNETCORE_ENVIRONMENT=Development and DevelopmentAuth:Enabled=true.
app.MapGet("/auth/dev-login", async (
    [FromQuery] string? returnUrl,
    [FromServices] CustomAuthStateProvider authProvider,
    IConfiguration configuration) =>
{
    if (!app.Environment.IsDevelopment())
    {
        Logs.Warning("Blocked development login: environment is not Development");
        return Results.NotFound();
    }

    if (!configuration.GetValue<bool>("DevelopmentAuth:Enabled", false))
    {
        Logs.Warning("Blocked development login: DevelopmentAuth:Enabled is false");
        return Results.NotFound();
    }

    var username = configuration["DevelopmentAuth:Username"]?.Trim();
    if (string.IsNullOrWhiteSpace(username))
    {
        username = "dev-admin";
    }

    var result = await authProvider.AuthenticateAsDeveloperAsync(
        username: username,
        displayName: configuration["DevelopmentAuth:DisplayName"]?.Trim(),
        email: configuration["DevelopmentAuth:Email"]?.Trim(),
        avatarUrl: configuration["DevelopmentAuth:AvatarUrl"]?.Trim(),
        roles: configuration["DevelopmentAuth:Roles"] ?? "User,Admin",
        token: configuration["DevelopmentAuth:Token"] ?? "dev-token");

    if (!result.Success)
    {
        Logs.Error($"Development login failed for {username}: {result.ErrorMessage}");
        return Results.Problem("Failed to sign in with development account", statusCode: 500);
    }

    var target = NormalizeLocalReturnUrl(returnUrl);
    Logs.Info($"Development login succeeded for {username}; redirecting to {target}");
    return Results.Redirect(target);
});

// OAuth callback endpoint - handle authentication BEFORE any response starts
app.MapGet("/auth/callback", async (HttpContext context, IPlexAuthService plexAuth, [FromServices] CustomAuthStateProvider authProvider) =>
{
    try
    {
        PlexRequestsHosted.Utils.Logs.Info("OAuth callback endpoint hit");
        
        // Get return URL from query string
        // Local paths only (no absolute URLs / protocol-relative) so this can't be used as an open redirect.
        var returnUrl = context.Request.Query["returnUrl"].FirstOrDefault() ?? "/browse";
        if (!returnUrl.StartsWith('/') || returnUrl.StartsWith("//")
            || returnUrl == "/" || returnUrl.StartsWith("/login", StringComparison.OrdinalIgnoreCase))
            returnUrl = "/browse";

        // Prefer pinId from the query (set in forwardUrl). Fall back to server session if absent.
        int pinId;
        var pinIdQuery = context.Request.Query["pinId"].FirstOrDefault();
        if (!string.IsNullOrEmpty(pinIdQuery) && int.TryParse(pinIdQuery, out var pinFromQuery))
        {
            pinId = pinFromQuery;
        }
        else
        {
            var pinIdStr = context.Session.GetString("plex_pin_id");
            if (string.IsNullOrEmpty(pinIdStr) || !int.TryParse(pinIdStr, out pinId))
            {
                PlexRequestsHosted.Utils.Logs.Error("No PIN ID found (neither query nor session)");
                return Results.Redirect($"/login?error=no_pin&returnUrl={Uri.EscapeDataString(returnUrl)}");
            }
        }

        // Check PIN status
        var result = await plexAuth.PollForAuthenticationAsync(pinId);
        if (!result.Success || result.AuthToken == null)
        {
            PlexRequestsHosted.Utils.Logs.Error($"PIN authentication failed: {result.ErrorMessage}");
            return Results.Redirect($"/login?error=auth_failed&returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        // Authenticate with the token - this will set the cookie
        var authResult = await authProvider.AuthenticateWithPlexAsync(result.AuthToken, result.User?.Username ?? "plex-user");
        if (!authResult.Success)
        {
            PlexRequestsHosted.Utils.Logs.Error($"Authentication completion failed: {authResult.ErrorMessage}");
            return Results.Redirect($"/login?error=auth_completion_failed&returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        // Clear the PIN ID from session if it exists
        context.Session.Remove("plex_pin_id");
        
        PlexRequestsHosted.Utils.Logs.Info($"Authentication successful, redirecting to {returnUrl}");
        return Results.Redirect(returnUrl);
    }
    catch (Exception ex)
    {
        PlexRequestsHosted.Utils.Logs.Error($"OAuth callback error: {ex}");
        return Results.Redirect("/login?error=callback_error");
    }
});

// Start log saving
PlexRequestsHosted.Utils.Logs.StartLogSaving();

app.Run();
