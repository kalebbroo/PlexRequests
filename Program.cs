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
// HTTP client for services that depend on HttpClient
builder.Services.AddHttpClient();

// Options/config
builder.Services.Configure<PlexRequestsHosted.Services.Implementations.PlexConfiguration>(
    builder.Configuration.GetSection("Plex"));
builder.Services.AddMemoryCache();

// Core domain services
builder.Services.AddScoped<IPlexApiService, PlexApiService>();
builder.Services.AddScoped<IMediaRequestService, MediaRequestService>();

// AuthN/AuthZ
builder.Services.AddAuthorization();
builder.Services.AddScoped<AuthenticationStateProvider, CustomAuthStateProvider>();

// App service registrations (stubs for now)
builder.Services.AddScoped<IMediaRequestService, MediaRequestService>();
builder.Services.AddScoped<IPlexApiService, PlexApiService>();
builder.Services.AddScoped<IPlexAuthService, PlexAuthService>();
builder.Services.AddScoped<IUserProfileService, UserProfileService>();
builder.Services.AddScoped<IToastService, ToastService>();
builder.Services.AddScoped<IThemeService, ThemeService>();
builder.Services.AddScoped<IAuthService, AuthService>();

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


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode();

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
        }
    }
    catch { /* best-effort dev safeguard */ }
}

// Start log saving
PlexRequestsHosted.Utils.Logs.StartLogSaving();

app.Run();
