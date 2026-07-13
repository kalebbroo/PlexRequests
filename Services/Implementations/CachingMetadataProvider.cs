using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.MetadataProviders;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// DB-backed caching decorator over any <see cref="IMediaMetadataProvider"/>. Details, IMDb ids and
/// season episode lists are served from SQLite instantly (survive restarts); when a cached row is
/// stale it is returned immediately and refreshed in the background (stale-while-revalidate). Only a
/// true cache miss makes a blocking live call. Discovery/search lists forward to the inner provider
/// (which keeps its own in-memory cache). Uses <see cref="IDbContextFactory{AppDbContext}"/> so its
/// reads/writes never touch the request's scoped DbContext.
/// </summary>
public sealed class CachingMetadataProvider(
    IMediaMetadataProvider inner,
    IDbContextFactory<AppDbContext> dbf,
    IMetadataRefreshCoordinator refresh,
    ILogger<CachingMetadataProvider> log) : IMediaMetadataProvider
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan OngoingTtl = TimeSpan.FromHours(6);   // Status == "Returning Series"
    private static readonly TimeSpan EndedTtl = TimeSpan.FromDays(30);     // movies / ended shows
    private static readonly TimeSpan EpisodeTtl = TimeSpan.FromHours(12);

    // ---- cached read-through methods ----

    public async Task<MediaDetailDto?> GetDetailsAsync(int mediaId, MediaType mediaType)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var row = await db.MediaMetadataCache.AsNoTracking()
            .FirstOrDefaultAsync(x => x.MediaType == mediaType && x.TmdbId == mediaId);

        if (row?.DetailJson is { Length: > 0 })
        {
            var cached = TryDeserialize<MediaDetailDto>(row.DetailJson);
            if (cached is not null)
            {
                var ttl = row.Status == "Returning Series" ? OngoingTtl : EndedTtl;
                if (row.DetailFetchedAt is { } dt && DateTime.UtcNow - dt > ttl)
                    refresh.QueueDetail(mediaType, mediaId);   // stale -> refresh off the request path
                return cached;
            }
        }

        // Miss (or un-deserializable blob): one live fetch, write-through, return.
        var dto = await inner.GetDetailsAsync(mediaId, mediaType);
        if (dto is not null && !string.IsNullOrWhiteSpace(dto.Title))
        {
            await using var wdb = await dbf.CreateDbContextAsync();
            await UpsertDetailAsync(wdb, mediaType, mediaId, dto);
        }
        return dto;
    }

    public async Task<string?> GetImdbIdAsync(int mediaId, MediaType mediaType)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var row = await db.MediaMetadataCache.FirstOrDefaultAsync(x => x.MediaType == mediaType && x.TmdbId == mediaId);
        if (!string.IsNullOrEmpty(row?.ImdbId)) return row.ImdbId;

        var imdb = await inner.GetImdbIdAsync(mediaId, mediaType);
        if (!string.IsNullOrEmpty(imdb))
        {
            if (row is null)
            {
                row = new MediaMetadataCacheEntity { MediaType = mediaType, TmdbId = mediaId, CardFetchedAt = DateTime.UtcNow };
                db.MediaMetadataCache.Add(row);
            }
            row.ImdbId = imdb;
            await SaveSafeAsync(db);
        }
        return imdb;
    }

    public async Task<List<EpisodeDto>> GetSeasonEpisodesAsync(int showId, int seasonNumber)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var row = await db.SeasonEpisodesCache.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ShowTmdbId == showId && x.SeasonNumber == seasonNumber);
        if (row is not null)
        {
            var cached = TryDeserialize<List<EpisodeDto>>(row.EpisodesJson);
            if (cached is not null)
            {
                if (DateTime.UtcNow - row.FetchedAt > EpisodeTtl) refresh.QueueEpisodes(showId, seasonNumber);
                return cached;
            }
        }

        var eps = await inner.GetSeasonEpisodesAsync(showId, seasonNumber);
        if (eps.Count > 0)
        {
            await using var wdb = await dbf.CreateDbContextAsync();
            await UpsertEpisodesAsync(wdb, showId, seasonNumber, eps);
        }
        return eps;
    }

    // ---- forwarders (discovery/search stay in the inner's in-memory cache) ----
    public Task<List<MediaCardDto>> SearchAsync(string query, MediaType? mediaType = null, int page = 1, int pageSize = 20)
        => inner.SearchAsync(query, mediaType, page, pageSize);
    public Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10) => inner.GetRecentlyAddedAsync(count);
    public Task<List<MediaCardDto>> GetLibraryAsync(MediaType mediaType, int page = 1, int pageSize = 20) => inner.GetLibraryAsync(mediaType, page, pageSize);
    public Task<List<MediaCardDto>> GetTrendingAsync(MediaType? mediaType = null, int page = 1, int pageSize = 20) => inner.GetTrendingAsync(mediaType, page, pageSize);
    public Task<List<MediaCardDto>> GetPopularAsync(MediaType mediaType, int page = 1, int pageSize = 20) => inner.GetPopularAsync(mediaType, page, pageSize);
    public Task<List<MediaCardDto>> GetTopRatedAsync(MediaType mediaType, int page = 1, int pageSize = 20) => inner.GetTopRatedAsync(mediaType, page, pageSize);
    public Task<List<MediaCardDto>> GetByGenreAsync(MediaType mediaType, string genre, int page = 1, int pageSize = 20) => inner.GetByGenreAsync(mediaType, genre, page, pageSize);
    public Task<List<MediaCardDto>> GetSimilarAsync(int mediaId, MediaType mediaType, int count = 12) => inner.GetSimilarAsync(mediaId, mediaType, count);

    // ---- upsert / mapping helpers (also used by MetadataRefreshCoordinator) ----

    internal static async Task UpsertDetailAsync(AppDbContext db, MediaType mediaType, int tmdbId, MediaDetailDto dto)
    {
        var row = await db.MediaMetadataCache.FirstOrDefaultAsync(x => x.MediaType == mediaType && x.TmdbId == tmdbId);
        if (row is null)
        {
            row = new MediaMetadataCacheEntity { MediaType = mediaType, TmdbId = tmdbId };
            db.MediaMetadataCache.Add(row);
        }
        row.Title = dto.Title ?? string.Empty;
        row.Overview = dto.Overview;
        row.PosterUrl = dto.PosterUrl;
        row.BackdropUrl = dto.BackdropUrl;
        row.Year = dto.Year;
        row.Rating = dto.Rating;
        row.Runtime = dto.Runtime;
        row.GenresCsv = dto.Genres is { Count: > 0 } ? string.Join(",", dto.Genres) : string.Empty;
        row.TotalSeasons = dto.TotalSeasons;
        row.Status = dto.Status;
        if (!string.IsNullOrEmpty(dto.ImdbId)) row.ImdbId = dto.ImdbId;
        row.DetailJson = JsonSerializer.Serialize(dto, Json);
        row.DetailFetchedAt = DateTime.UtcNow;
        row.CardFetchedAt = DateTime.UtcNow;
        await SaveSafeAsync(db);
    }

    internal static async Task UpsertEpisodesAsync(AppDbContext db, int showTmdbId, int seasonNumber, List<EpisodeDto> episodes)
    {
        var row = await db.SeasonEpisodesCache.FirstOrDefaultAsync(x => x.ShowTmdbId == showTmdbId && x.SeasonNumber == seasonNumber);
        if (row is null)
        {
            row = new SeasonEpisodesCacheEntity { ShowTmdbId = showTmdbId, SeasonNumber = seasonNumber };
            db.SeasonEpisodesCache.Add(row);
        }
        row.EpisodesJson = JsonSerializer.Serialize(episodes, Json);
        row.FetchedAt = DateTime.UtcNow;
        await SaveSafeAsync(db);
    }

    /// <summary>Project a cache row into a lightweight card (for batched list reads).</summary>
    internal static MediaCardDto ToCard(MediaMetadataCacheEntity row) => new()
    {
        Id = row.TmdbId,
        TmdbId = row.TmdbId,
        ImdbId = row.ImdbId,
        MediaType = row.MediaType,
        Title = row.Title,
        Overview = row.Overview,
        PosterUrl = row.PosterUrl,
        BackdropUrl = row.BackdropUrl,
        Year = row.Year,
        Rating = row.Rating,
        Runtime = row.Runtime,
        Genres = string.IsNullOrEmpty(row.GenresCsv) ? new() : row.GenresCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        TotalSeasons = row.TotalSeasons,
        RequestStatus = RequestStatus.None
    };

    private static async Task SaveSafeAsync(AppDbContext db)
    {
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { /* concurrent insert of the same key — the other writer won; fine */ }
    }

    private static T? TryDeserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch { return default; }
    }
}

/// <summary>
/// Singleton fire-and-forget background refresher for stale cache rows. Dedupes by key so a burst of
/// readers triggers one live fetch. Runs in its own DI scope and resolves the RAW inner provider (via
/// the factory) — never the caching decorator — to avoid recursion.
/// </summary>
public sealed class MetadataRefreshCoordinator(
    IServiceScopeFactory scopes,
    ILogger<MetadataRefreshCoordinator> log) : IMetadataRefreshCoordinator
{
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    public void QueueDetail(MediaType mediaType, int tmdbId)
    {
        var key = $"d:{mediaType}:{tmdbId}";
        if (!_inFlight.TryAdd(key, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var innerProvider = scope.ServiceProvider.GetRequiredService<IMetadataProviderFactory>().GetDefaultProvider();
                var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                var dto = await innerProvider.GetDetailsAsync(tmdbId, mediaType);
                if (dto is not null && !string.IsNullOrWhiteSpace(dto.Title))
                {
                    await using var db = await dbf.CreateDbContextAsync();
                    await CachingMetadataProvider.UpsertDetailAsync(db, mediaType, tmdbId, dto);
                }
            }
            catch (Exception ex) { log.LogWarning(ex, "Background detail refresh failed for {Type} {Id}", mediaType, tmdbId); }
            finally { _inFlight.TryRemove(key, out _); }
        });
    }

    public void QueueEpisodes(int showTmdbId, int seasonNumber)
    {
        var key = $"e:{showTmdbId}:{seasonNumber}";
        if (!_inFlight.TryAdd(key, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var innerProvider = scope.ServiceProvider.GetRequiredService<IMetadataProviderFactory>().GetDefaultProvider();
                var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                var eps = await innerProvider.GetSeasonEpisodesAsync(showTmdbId, seasonNumber);
                if (eps.Count > 0)
                {
                    await using var db = await dbf.CreateDbContextAsync();
                    await CachingMetadataProvider.UpsertEpisodesAsync(db, showTmdbId, seasonNumber, eps);
                }
            }
            catch (Exception ex) { log.LogWarning(ex, "Background episode refresh failed for {Show} S{Season}", showTmdbId, seasonNumber); }
            finally { _inFlight.TryRemove(key, out _); }
        });
    }
}
