using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.Enums;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// Database-backed <see cref="IIndexerAdminService"/>. Rows are seeded for the built-in implementations so
/// the panel isn't empty before the downloader's first search; admins add Torznab endpoints on top, each as
/// its own row with its own URL, key, categories, priority and health.
///
/// API keys are encrypted at rest with the app's data-protection keyring — the same treatment network-share
/// passwords get — and only decrypted when handed to the downloader over the authenticated worker API.
/// </summary>
public class IndexerAdminService(AppDbContext db, IDataProtectionProvider dp, ILogger<IndexerAdminService> logger)
    : IIndexerAdminService
{
    private readonly IDataProtector _protector = dp.CreateProtector("IndexerCredentials.v1");

    /// <summary>
    /// The built-in implementations, with the capabilities that used to be hardcoded in each provider class.
    /// Seeded once; afterwards these are ordinary rows an admin can disable, re-prioritise or re-scope.
    /// </summary>
    private static readonly (string Name, bool Movie, bool Tv, bool Anime, bool Music, bool AnimeOnly)[] BuiltIns =
    {
        ("EZTV",        false, true,  true,  false, false),
        ("YTS",         true,  false, false, false, false),
        ("1337x",       true,  true,  true,  true,  false),
        ("Nyaa",        true,  true,  true,  false, true),   // anime-only: skipped unless the job is classified anime
        ("ext.to",      true,  true,  true,  true,  false),
        ("PirateBay",   true,  true,  true,  true,  false),
        ("TorrentsCSV", true,  true,  true,  true,  false),
    };

    public async Task<List<IndexerSettingDto>> GetAllAsync()
    {
        await SeedAsync();
        var rows = await db.Indexers.AsNoTracking()
            .Include(i => i.MediaCapabilities)
            .OrderBy(i => i.Priority).ThenBy(i => i.Name)
            .ToListAsync();
        return rows.Select(ToDto).ToList();
    }

    public async Task<bool> SetEnabledAsync(string name, bool enabled)
    {
        var row = await db.Indexers.FirstOrDefaultAsync(i => i.Name == name);
        if (row is null) return false;
        row.Enabled = enabled;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        logger.LogInformation("Indexer \"{Name}\" {State} by admin", name, enabled ? "enabled" : "disabled");
        return true;
    }

    public async Task<bool> SetPriorityAsync(string name, int priority)
    {
        var row = await db.Indexers.FirstOrDefaultAsync(i => i.Name == name);
        if (row is null) return false;
        row.Priority = Math.Clamp(priority, 1, 50);
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    // ---- CRUD ---------------------------------------------------------------------------------------

    public async Task<(bool ok, string? error)> SaveAsync(IndexerSettingDto dto)
    {
        var isNew = dto.Id == 0;
        var row = isNew ? new IndexerEntity() : await db.Indexers.Include(i => i.MediaCapabilities)
            .FirstOrDefaultAsync(i => i.Id == dto.Id);
        if (row is null) return (false, "Indexer not found.");

        var name = (dto.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) return (false, "A name is required.");
        if (await db.Indexers.AnyAsync(i => i.Name == name && i.Id != dto.Id))
            return (false, $"An indexer named \"{name}\" already exists.");

        // Only built-ins may omit a URL — they know their own endpoints.
        var implementation = string.IsNullOrWhiteSpace(dto.Implementation) ? "Torznab" : dto.Implementation.Trim();
        var isBuiltIn = isNew ? false : row.IsBuiltIn;
        if (!isBuiltIn && string.IsNullOrWhiteSpace(dto.Url))
            return (false, "A Torznab/Newznab indexer needs its API URL.");

        row.Name = name;
        if (!isBuiltIn)
        {
            row.Implementation = implementation;
            row.Url = dto.Url?.Trim();
        }
        row.Enabled = dto.Enabled;
        row.Priority = Math.Clamp(dto.Priority, 1, 50);
        row.MovieCategoriesCsv = Blank(dto.MovieCategoriesCsv);
        row.TvCategoriesCsv = Blank(dto.TvCategoriesCsv);
        row.AnimeCategoriesCsv = Blank(dto.AnimeCategoriesCsv);
        row.SupportsMovie = dto.SupportsMovie;
        row.SupportsTv = dto.SupportsTv;
        row.SupportsAnime = dto.SupportsAnime;
        row.AnimeOnly = dto.AnimeOnly;
        SyncCapabilities(row, dto);
        row.EnableIngestion = dto.EnableIngestion;
        row.EnableAutomaticSearch = dto.EnableAutomaticSearch;
        row.EnableInteractiveSearch = dto.EnableInteractiveSearch;
        row.EnableRecommendedFeed = dto.EnableRecommendedFeed;
        row.MinSeeders = dto.MinSeeders;
        row.TimeoutSeconds = dto.TimeoutSeconds;
        row.RateLimitPerMinute = Math.Clamp(dto.RateLimitPerMinute <= 0 ? 30 : dto.RateLimitPerMinute, 1, 600);
        row.CacheSeconds = Math.Clamp(dto.CacheSeconds, 0, 3600);
        row.MaxDetailFetches = Math.Clamp(dto.MaxDetailFetches <= 0 ? 25 : dto.MaxDetailFetches, 1, 100);
        row.MaxAgeDays = dto.MaxAgeDays;
        row.BaseUrlOverride = Blank(dto.BaseUrlOverride);
        row.Notes = Blank(dto.Notes);

        // An empty key on an edit means "leave it alone" — the UI never round-trips the real value, so
        // treating blank as "clear it" would silently wipe the key every time anything else was saved.
        if (!string.IsNullOrWhiteSpace(dto.ApiKey))
            row.ApiKeyEncrypted = _protector.Protect(dto.ApiKey.Trim());

        // Clearance is a pair: the cookie is bound to the User-Agent that earned it, so setting one without
        // the other produces a combination Cloudflare will reject. Stamped with a time so the panel can show
        // how stale it is — these expire, typically within a day.
        if (!string.IsNullOrWhiteSpace(dto.ClearanceCookie))
        {
            row.ClearanceCookieEncrypted = _protector.Protect(dto.ClearanceCookie.Trim());
            row.ClearanceObtainedAt = DateTime.UtcNow;
        }
        else if (dto.ClearClearance)
        {
            row.ClearanceCookieEncrypted = null;
            row.ClearanceObtainedAt = null;
        }
        if (dto.UserAgent is not null) row.UserAgent = Blank(dto.UserAgent);

        row.UpdatedAt = DateTime.UtcNow;
        if (isNew)
        {
            row.IsBuiltIn = false;
            row.CreatedAt = DateTime.UtcNow;
            db.Indexers.Add(row);
        }
        await db.SaveChangesAsync();
        logger.LogInformation("Indexer \"{Name}\" ({Impl}) {Action} by admin", row.Name, row.Implementation, isNew ? "added" : "updated");
        return (true, null);
    }

    public async Task<(bool ok, string? error)> DeleteAsync(int id)
    {
        var row = await db.Indexers.FirstOrDefaultAsync(i => i.Id == id);
        if (row is null) return (false, "Indexer not found.");
        // Built-ins are re-seeded on the next panel load, so deleting one is at best a no-op and at worst
        // loses its accumulated health history. Disabling is the supported way to take one out of rotation.
        if (row.IsBuiltIn) return (false, "Built-in indexers can't be deleted — disable it instead.");
        db.Indexers.Remove(row);
        await db.SaveChangesAsync();
        logger.LogInformation("Indexer \"{Name}\" deleted by admin", row.Name);
        return (true, null);
    }

    /// <summary>
    /// One-time import of Torznab endpoints configured in the downloader's environment. The downloader has to
    /// push these because they live in ITS environment (Indexer__Torznab__0__Url and friends) and the web
    /// container can't read them. Idempotent by normalized URL so a restart can't duplicate rows.
    /// </summary>
    public async Task<int> ImportLegacyTorznabAsync(List<LegacyTorznabEndpointDto> endpoints)
    {
        if (endpoints.Count == 0) return 0;
        await SeedAsync();

        var existing = await db.Indexers.Where(i => i.Url != null)
            .Select(i => new { i.Id, i.Url }).ToListAsync();
        var known = existing.Select(e => Normalize(e.Url!)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        int added = 0;
        var now = DateTime.UtcNow;
        foreach (var e in endpoints)
        {
            if (string.IsNullOrWhiteSpace(e.Url) || !known.Add(Normalize(e.Url))) continue;

            var name = string.IsNullOrWhiteSpace(e.Name) ? "Torznab" : e.Name.Trim();
            // Keep names unique without failing the whole import over a collision.
            var candidate = name;
            for (int n = 2; await db.Indexers.AnyAsync(i => i.Name == candidate); n++) candidate = $"{name} ({n})";

            var entity = new IndexerEntity
            {
                Name = candidate,
                Implementation = "Torznab",
                IsBuiltIn = false,
                Enabled = e.Enabled,
                Priority = 10, // ahead of the public built-ins: an aggregator is the better source
                Url = e.Url.Trim(),
                ApiKeyEncrypted = string.IsNullOrWhiteSpace(e.ApiKey) ? null : _protector.Protect(e.ApiKey.Trim()),
                CreatedAt = now,
                UpdatedAt = now
            };
            AddDefaultCapabilities(entity, movie: true, tv: true, anime: true, music: false);
            db.Indexers.Add(entity);
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync();
            logger.LogInformation("Imported {Count} Torznab endpoint(s) from the downloader's configuration", added);
        }
        return added;
    }

    // ---- Worker config + telemetry ------------------------------------------------------------------

    public async Task<List<IndexerConfigDto>> GetWorkerConfigAsync()
    {
        await SeedAsync();
        var rows = await db.Indexers.AsNoTracking().Include(i => i.MediaCapabilities)
            .OrderBy(i => i.Priority).ToListAsync();
        return rows.Select(i => new IndexerConfigDto
        {
            Id = i.Id,
            Name = i.Name,
            Implementation = i.Implementation,
            Enabled = i.Enabled,
            Priority = i.Priority,
            Url = i.Url,
            ApiKey = Unprotect(i.ApiKeyEncrypted),
            BaseUrlOverride = i.BaseUrlOverride,
            ClearanceCookie = Unprotect(i.ClearanceCookieEncrypted),
            UserAgent = i.UserAgent,
            MovieCategoriesCsv = i.MovieCategoriesCsv,
            TvCategoriesCsv = i.TvCategoriesCsv,
            AnimeCategoriesCsv = i.AnimeCategoriesCsv,
            SupportsMovie = i.SupportsMovie,
            SupportsTv = i.SupportsTv,
            SupportsAnime = i.SupportsAnime,
            AnimeOnly = i.AnimeOnly,
            MediaCapabilities = i.MediaCapabilities.Select(ToCapabilityDto).ToList(),
            EnableIngestion = i.EnableIngestion,
            EnableAutomaticSearch = i.EnableAutomaticSearch,
            EnableInteractiveSearch = i.EnableInteractiveSearch,
            EnableRecommendedFeed = i.EnableRecommendedFeed,
            SearchCircuitOpenUntil = i.SearchCircuitOpenUntil,
            MinSeeders = i.MinSeeders,
            TimeoutSeconds = i.TimeoutSeconds,
            RateLimitPerMinute = i.RateLimitPerMinute,
            CacheSeconds = i.CacheSeconds,
            MaxDetailFetches = i.MaxDetailFetches,
            MaxAgeDays = i.MaxAgeDays
        }).ToList();
    }

    public async Task ReportStatusAsync(List<IndexerStatusReportDto> reports)
    {
        if (reports.Count == 0) return;
        var now = DateTime.UtcNow;

        // Prefer the id — names are now admin-editable, so keying on them alone would orphan a row's history
        // the moment someone renamed it.
        var ids = reports.Where(r => r.IndexerId > 0).Select(r => r.IndexerId).Distinct().ToList();
        var names = reports.Select(r => r.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        var rows = await db.Indexers.Where(i => ids.Contains(i.Id) || names.Contains(i.Name)).ToListAsync();
        var byId = rows.ToDictionary(i => i.Id);
        var byName = rows.GroupBy(i => i.Name).ToDictionary(g => g.Key, g => g.First());

        foreach (var r in reports)
        {
            IndexerEntity? row = null;
            if (r.IndexerId > 0) byId.TryGetValue(r.IndexerId, out row);
            if (row is null && !string.IsNullOrWhiteSpace(r.Name)) byName.TryGetValue(r.Name, out row);
            if (row is null)
            {
                if (string.IsNullOrWhiteSpace(r.Name)) continue;
                row = new IndexerEntity { Name = r.Name, Implementation = r.Name, IsBuiltIn = true, CreatedAt = now };
                db.Indexers.Add(row);
                byName[r.Name] = row;
            }

            row.LastSearchAt = r.SearchedAt == default ? now : r.SearchedAt;
            row.LastResultCount = r.ResultCount;
            row.LastLatencyMs = r.LatencyMs;
            row.TotalSearches++;
            row.TotalResults += r.ResultCount;
            if (r.Success)
            {
                row.LastSuccessAt = row.LastSearchAt;
                row.ConsecutiveFailures = 0;
                row.LastError = null;
                row.SearchCircuitOpenUntil = null;
            }
            else
            {
                row.ConsecutiveFailures++;
                row.LastError = r.Error is { Length: > 512 } e ? e[..512] : r.Error;
                if (r.BlockReason != IndexerBlockReason.None)
                {
                    row.LastBlockReason = r.BlockReason;
                    row.LastBlockedAt = row.LastSearchAt;
                    row.SearchCircuitOpenUntil = row.LastSearchAt + SearchFailureDelay(r.BlockReason, row.ConsecutiveFailures);
                }
                else if (row.ConsecutiveFailures >= 3)
                    row.SearchCircuitOpenUntil = row.LastSearchAt + SearchFailureDelay(IndexerBlockReason.None, row.ConsecutiveFailures);
            }
            // A block clears only on a genuine success, so the panel keeps showing "Blocked by Cloudflare"
            // until the indexer actually answers again rather than flickering on the next unrelated error.
            if (r.Success) { row.LastBlockReason = IndexerBlockReason.None; row.LastBlockedAt = null; }
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync();
    }

    // ---- Seeding ------------------------------------------------------------------------------------

    private async Task SeedAsync()
    {
        var existing = await db.Indexers.Select(i => i.Name).ToListAsync();
        var now = DateTime.UtcNow;
        foreach (var b in BuiltIns.Where(b => !existing.Contains(b.Name)))
        {
            var entity = new IndexerEntity
            {
                Name = b.Name,
                Implementation = b.Name,
                IsBuiltIn = true,
                SupportsMovie = b.Movie,
                SupportsTv = b.Tv,
                SupportsAnime = b.Anime,
                AnimeOnly = b.AnimeOnly,
                Priority = 30, // behind admin-added aggregators by default
                CreatedAt = now,
                UpdatedAt = now
            };
            AddDefaultCapabilities(entity, b.Movie, b.Tv, b.Anime, b.Music);
            db.Indexers.Add(entity);
        }
        if (!db.ChangeTracker.HasChanges()) return;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            // Two callers seeded concurrently and the unique Name index rejected the duplicate — the rows
            // exist either way, so drop ours and carry on.
            foreach (var entry in db.ChangeTracker.Entries<IndexerEntity>().Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private static IndexerSettingDto ToDto(IndexerEntity i) => new()
    {
        Id = i.Id, Name = i.Name, Implementation = i.Implementation, IsBuiltIn = i.IsBuiltIn,
        Enabled = i.Enabled, Priority = i.Priority, Url = i.Url,
        // The stored key is never round-tripped to the browser — only whether one is set.
        HasApiKey = !string.IsNullOrWhiteSpace(i.ApiKeyEncrypted),
        BaseUrlOverride = i.BaseUrlOverride,
        MovieCategoriesCsv = i.MovieCategoriesCsv, TvCategoriesCsv = i.TvCategoriesCsv,
        AnimeCategoriesCsv = i.AnimeCategoriesCsv,
        SupportsMovie = i.SupportsMovie, SupportsTv = i.SupportsTv, SupportsAnime = i.SupportsAnime,
        AnimeOnly = i.AnimeOnly,
        MediaCapabilities = i.MediaCapabilities.Select(ToCapabilityDto).ToList(),
        EnableIngestion = i.EnableIngestion, EnableAutomaticSearch = i.EnableAutomaticSearch,
        EnableInteractiveSearch = i.EnableInteractiveSearch, EnableRecommendedFeed = i.EnableRecommendedFeed,
        MinSeeders = i.MinSeeders, TimeoutSeconds = i.TimeoutSeconds,
        RateLimitPerMinute = i.RateLimitPerMinute, CacheSeconds = i.CacheSeconds,
        MaxDetailFetches = i.MaxDetailFetches, MaxAgeDays = i.MaxAgeDays, Notes = i.Notes,
        LastSearchAt = i.LastSearchAt, LastResultCount = i.LastResultCount,
        LastSuccessAt = i.LastSuccessAt, LastError = i.LastError,
        ConsecutiveFailures = i.ConsecutiveFailures,
        LastBlockReason = i.LastBlockReason, LastBlockedAt = i.LastBlockedAt,
        SearchCircuitOpenUntil = i.SearchCircuitOpenUntil,
        HasClearance = !string.IsNullOrWhiteSpace(i.ClearanceCookieEncrypted),
        ClearanceObtainedAt = i.ClearanceObtainedAt,
        UserAgent = i.UserAgent,
        TotalSearches = i.TotalSearches, TotalResults = i.TotalResults, LastLatencyMs = i.LastLatencyMs
    };

    private string? Unprotect(string? encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted)) return null;
        // A rotated/lost keyring shouldn't take the whole worker config down — the indexer just searches
        // unauthenticated and fails visibly in its own health row.
        try { return _protector.Unprotect(encrypted); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not decrypt an indexer API key; it will need re-entering"); return null; }
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static IndexerMediaCapabilityDto ToCapabilityDto(IndexerMediaCapabilityEntity capability) => new()
    {
        MediaType = capability.MediaType,
        Enabled = capability.Enabled,
        CategoriesCsv = capability.CategoriesCsv
    };

    private static void SyncCapabilities(IndexerEntity row, IndexerSettingDto dto)
    {
        var supplied = dto.MediaCapabilities.Count > 0
            ? dto.MediaCapabilities.ToDictionary(x => x.MediaType)
            : new Dictionary<MediaType, IndexerMediaCapabilityDto>
            {
                [MediaType.Movie] = new() { MediaType = MediaType.Movie, Enabled = dto.SupportsMovie, CategoriesCsv = dto.MovieCategoriesCsv },
                [MediaType.TvShow] = new() { MediaType = MediaType.TvShow, Enabled = dto.SupportsTv, CategoriesCsv = dto.TvCategoriesCsv },
                [MediaType.Anime] = new() { MediaType = MediaType.Anime, Enabled = dto.SupportsAnime, CategoriesCsv = dto.AnimeCategoriesCsv },
                [MediaType.Music] = new() { MediaType = MediaType.Music, Enabled = false, CategoriesCsv = null }
            };

        foreach (var mediaType in Enum.GetValues<MediaType>())
        {
            supplied.TryGetValue(mediaType, out var input);
            var capability = row.MediaCapabilities.FirstOrDefault(x => x.MediaType == mediaType);
            if (capability is null)
            {
                capability = new IndexerMediaCapabilityEntity { MediaType = mediaType };
                row.MediaCapabilities.Add(capability);
            }
            capability.Enabled = input?.Enabled ?? false;
            capability.CategoriesCsv = Blank(input?.CategoriesCsv);
        }

        // Transitional mirrors for older downloader containers. They can be removed once bridge/worker v2
        // has been deployed everywhere; new code reads MediaCapabilities first.
        row.SupportsMovie = row.MediaCapabilities.Any(x => x.MediaType == MediaType.Movie && x.Enabled);
        row.SupportsTv = row.MediaCapabilities.Any(x => x.MediaType == MediaType.TvShow && x.Enabled);
        row.SupportsAnime = row.MediaCapabilities.Any(x => x.MediaType == MediaType.Anime && x.Enabled);
        row.MovieCategoriesCsv = row.MediaCapabilities.First(x => x.MediaType == MediaType.Movie).CategoriesCsv;
        row.TvCategoriesCsv = row.MediaCapabilities.First(x => x.MediaType == MediaType.TvShow).CategoriesCsv;
        row.AnimeCategoriesCsv = row.MediaCapabilities.First(x => x.MediaType == MediaType.Anime).CategoriesCsv;
    }

    private static void AddDefaultCapabilities(IndexerEntity entity, bool movie, bool tv, bool anime, bool music)
    {
        entity.MediaCapabilities.Add(new() { MediaType = MediaType.Movie, Enabled = movie });
        entity.MediaCapabilities.Add(new() { MediaType = MediaType.TvShow, Enabled = tv });
        entity.MediaCapabilities.Add(new() { MediaType = MediaType.Anime, Enabled = anime });
        entity.MediaCapabilities.Add(new() { MediaType = MediaType.Music, Enabled = music, CategoriesCsv = music ? "3000" : null });
    }

    internal static TimeSpan SearchFailureDelay(IndexerBlockReason reason, int consecutiveFailures) =>
        IndexerCircuitPolicy.FailureDelay(reason, consecutiveFailures);

    /// <summary>Compare endpoint URLs ignoring trailing slashes and case, so the legacy import can't
    /// re-add an endpoint that's already present under a cosmetically different URL.</summary>
    private static string Normalize(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();
}
