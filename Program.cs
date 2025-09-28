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
using System.IO;
using System.Net.Http;
using System.Net.Security;
using PlexRequestsHosted.Shared.Enums;
using Microsoft.AspNetCore.Authorization;

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
    }
}

// Preload .env before configuration is built so env vars flow into builder.Configuration
LoadDotEnvFrom(Directory.GetCurrentDirectory());

var builder = WebApplication.CreateBuilder(args);
// Also attempt loading after, using content root
LoadDotEnvFrom(builder.Environment.ContentRootPath);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// UI and storage services for client interactivity
builder.Services.AddMudServices();
builder.Services.AddBlazoredLocalStorage();
builder.Services.AddBlazoredSessionStorage();
builder.Services.AddSignalR();
// HTTP client for services that depend on HttpClient
builder.Services.AddHttpClient();
// HttpContext accessor for cookie sign-in from AuthStateProvider
builder.Services.AddHttpContextAccessor();

// Options/config
builder.Services.Configure<PlexRequestsHosted.Services.Implementations.PlexConfiguration>(
    builder.Configuration.GetSection("Plex"));
builder.Services.AddMemoryCache();

// Core domain services
// Configure typed HttpClient for PlexApiService with optional invalid cert allowance (for self-signed or IP-based SSL)
var plexSection = builder.Configuration.GetSection("Plex");
var allowInvalidCerts = plexSection.GetValue<bool>("AllowInvalidCerts");
builder.Services.AddHttpClient<IPlexApiService, PlexApiService>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) =>
            allowInvalidCerts ? true : errors == SslPolicyErrors.None
    });
builder.Services.AddScoped<IMediaRequestService, MediaRequestService>();

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
    // Do NOT set a global fallback policy; components use [Authorize] via _Imports.razor.
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});
builder.Services.AddScoped<AuthenticationStateProvider, CustomAuthStateProvider>();

// App service registrations (stubs for now)
// (Removed duplicate registrations of IPlexApiService/IMediaRequestService)
builder.Services.AddScoped<IPlexAuthService, PlexAuthService>();
builder.Services.AddScoped<IUserProfileService, UserProfileService>();
builder.Services.AddScoped<IToastService, ToastService>();
builder.Services.AddScoped<IThemeService, ThemeService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddSingleton<PlexRequestsHosted.Services.Abstractions.INotificationService, PlexRequestsHosted.Services.Implementations.NotificationService>();

// Metadata providers
builder.Services.AddScoped<TmdbMetadataProvider>();
builder.Services.AddScoped<TraktMetadataProvider>();
builder.Services.AddScoped<SeedMetadataProvider>();
builder.Services.AddScoped<IMetadataProviderFactory, MetadataProviderFactory>();

// Use factory to get default provider
builder.Services.AddScoped<IMediaMetadataProvider>(sp =>
    sp.GetRequiredService<IMetadataProviderFactory>().GetDefaultProvider());

// Persistence: SQLite
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=app.db"));

var app = builder.Build();

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

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode();

// Hubs
app.MapHub<PlexRequestsHosted.Hubs.NotificationsHub>("/hubs/notifications").RequireAuthorization();

// Ensure database exists on startup (demo friendly)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // Lightweight dev-time schema guard: create UserProfiles table if missing (SQLite)
    try
    {
        var exists = db.Database.ExecuteSqlRaw(@"SELECT 1 FROM sqlite_master WHERE type='table' AND name='UserProfiles'");
        // ExecuteSqlRaw returns -1 for SELECT; query scalar result via raw ADO to be accurate
        using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='UserProfiles'";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        if (count == 0)
        {
            using var create = conn.CreateCommand();
            create.CommandText = @"CREATE TABLE UserProfiles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL UNIQUE,
                PlexId TEXT NULL,
                PlexUsername TEXT NULL,
                Roles TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                LastLoginAt TEXT NULL,
                ThemeDarkMode INTEGER NOT NULL,
                Language TEXT NULL,
                Region TEXT NULL,
                ShowAdultContent INTEGER NOT NULL,
                DefaultSort INTEGER NOT NULL,
                DefaultQualityMovie INTEGER NOT NULL,
                DefaultQualityTV INTEGER NOT NULL,
                AutoplayTrailers INTEGER NOT NULL,
                WatchedBadges INTEGER NOT NULL,
                PreferredProvider TEXT NOT NULL,
                MovieRequestLimit INTEGER NULL,
                TvRequestLimit INTEGER NULL,
                MusicRequestLimit INTEGER NULL,
                WhitelistStatus TEXT NOT NULL,
                PreferredServerMachineId TEXT NULL,
                PreferredServerName TEXT NULL,
                FOREIGN KEY(UserId) REFERENCES Users(Id) ON DELETE CASCADE
            );";
            await create.ExecuteNonQueryAsync();
        }
    }
    catch { /* best-effort dev safeguard */ }

    // Ensure new Discord fields exist on UserProfiles
    try
    {
        using var connProfiles = db.Database.GetDbConnection();
        await connProfiles.OpenAsync();
        using var cmdInfoProf = connProfiles.CreateCommand();
        cmdInfoProf.CommandText = "PRAGMA table_info('UserProfiles')";
        var cols = new List<string>();
        using (var reader = await cmdInfoProf.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                cols.Add(Convert.ToString(reader[1]) ?? string.Empty);
            }
        }
        async Task EnsureProfileColumnAsync(string name, string ddl)
        {
            if (!cols.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                using var cmdAlter = connProfiles.CreateCommand();
                cmdAlter.CommandText = $"ALTER TABLE UserProfiles ADD COLUMN {ddl}";
                await cmdAlter.ExecuteNonQueryAsync();
            }
        }
        await EnsureProfileColumnAsync("DiscordUserId", "DiscordUserId TEXT NULL");
        await EnsureProfileColumnAsync("DiscordUsername", "DiscordUsername TEXT NULL");
        await EnsureProfileColumnAsync("DiscordDmOptIn", "DiscordDmOptIn INTEGER NOT NULL DEFAULT 0");
    }
    catch { /* best-effort dev safeguard */ }

    // Ensure new columns exist on MediaRequests (SQLite) for full lifecycle fields
    try
    {
        using var conn2 = db.Database.GetDbConnection();
        await conn2.OpenAsync();
        using (var cmdInfo = conn2.CreateCommand())
        {
            cmdInfo.CommandText = "PRAGMA table_info('MediaRequests')";
            var cols = new List<string>();
            using var reader = await cmdInfo.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                cols.Add(Convert.ToString(reader[1]) ?? string.Empty);
            }
            async Task EnsureColumnAsync(string name, string ddl)
            {
                if (!cols.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    using var cmdAlter = conn2.CreateCommand();
                    cmdAlter.CommandText = $"ALTER TABLE MediaRequests ADD COLUMN {ddl}";
                    await cmdAlter.ExecuteNonQueryAsync();
                }
            }
            await EnsureColumnAsync("ApprovedAt", "ApprovedAt TEXT NULL");
            await EnsureColumnAsync("AvailableAt", "AvailableAt TEXT NULL");
            await EnsureColumnAsync("DenialReason", "DenialReason TEXT NULL");
            await EnsureColumnAsync("RequestNote", "RequestNote TEXT NULL");
            await EnsureColumnAsync("PosterUrl", "PosterUrl TEXT NULL");
            await EnsureColumnAsync("RequestAllSeasons", "RequestAllSeasons INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync("RequestedSeasonsCsv", "RequestedSeasonsCsv TEXT NULL");
        }
    }
    catch { /* best-effort dev safeguard */ }

    // Ensure PlexMappings table exists (SQLite) for GUID->ratingKey cache
    try
    {
        using var conn3 = db.Database.GetDbConnection();
        await conn3.OpenAsync();
        using (var cmdInfo = conn3.CreateCommand())
        {
            cmdInfo.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='PlexMappings'";
            var count = Convert.ToInt32(await cmdInfo.ExecuteScalarAsync());
            if (count == 0)
            {
                using var cmdCreate = conn3.CreateCommand();
                cmdCreate.CommandText = @"CREATE TABLE PlexMappings (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ExternalKey TEXT NOT NULL UNIQUE,
                    RatingKey TEXT NOT NULL,
                    MediaType INTEGER NULL,
                    Title TEXT NULL,
                    Year INTEGER NULL,
                    LastSeenAt TEXT NOT NULL
                );";
                await cmdCreate.ExecuteNonQueryAsync();

                using var cmdIdx1 = conn3.CreateCommand();
                cmdIdx1.CommandText = "CREATE INDEX IF NOT EXISTS IX_PlexMappings_RatingKey ON PlexMappings(RatingKey)";
                await cmdIdx1.ExecuteNonQueryAsync();
            }
        }
    }
    catch { /* best-effort dev safeguard */ }
}

// Simple health endpoint for Plex connectivity
app.MapGet("/api/plex/health", async (IPlexApiService plex) =>
{
    var info = await plex.GetServerInfoAsync();
    return Results.Ok(new { online = info?.IsOnline == true, name = info?.Name, version = info?.Version });
}).RequireAuthorization();

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

// Start log saving
PlexRequestsHosted.Utils.Logs.StartLogSaving();

app.Run();
