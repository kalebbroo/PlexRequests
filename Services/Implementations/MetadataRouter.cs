using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.MetadataProviders;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// The single metadata entry point ("one method for any media type"). Implements
/// <see cref="IMediaMetadataProvider"/> and routes each call to the provider the admin chose for that
/// media type (music uses the persisted catalog setting; other types use
/// <c>MetadataProviders:{Movie|TvShow|Anime}</c> or <c>:DefaultProvider</c>),
/// falling back to a keyless provider when the chosen one is unavailable, and finally to Seed. Consumers
/// keep injecting <see cref="IMediaMetadataProvider"/> (wrapped by the caching decorator); they never
/// pick a provider themselves.
///
/// Providers are resolved safely at construction — one that throws without its key (e.g. TMDb) is simply
/// omitted, so the router always has *something* to serve.
/// </summary>
public class MetadataRouter : IMediaMetadataProvider
{
    private readonly IConfiguration _config;
    private readonly ILogger<MetadataRouter> _logger;
    private readonly IMusicSettingsService _musicSettings;
    private readonly List<IMediaMetadataProvider> _providers = new();

    public MetadataRouter(IServiceProvider sp, IConfiguration config, ILogger<MetadataRouter> logger,
        IMusicSettingsService musicSettings)
    {
        _config = config;
        _logger = logger;
        _musicSettings = musicSettings;
        // Order matters only for the "first keyless / first supporting" fallbacks.
        foreach (var type in new[]
                 {
                     typeof(TmdbMetadataProvider), typeof(TvdbMetadataProvider), typeof(TraktMetadataProvider),
                     typeof(MusicBrainzMetadataProvider), typeof(YouTubeMusicMetadataProvider),
                     typeof(SeedMetadataProvider)
                 })
        {
            try
            {
                if (sp.GetService(type) is IMediaMetadataProvider p) _providers.Add(p);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Metadata provider {Type} unavailable (likely missing key) — skipping", type.Name);
            }
        }
        _logger.LogInformation("Metadata router active with providers: {Providers}",
            string.Join(", ", _providers.Select(p => $"{p.ProviderKey}{(p.IsAvailable ? "" : "(off)")}")));
    }

    /// <summary>Choose the provider for a media type: admin-configured, else any available real provider, else Seed.</summary>
    private IMediaMetadataProvider Pick(MediaType mediaType, string? requestedKey = null)
    {
        var supporting = _providers.Where(p => p.Supports(mediaType) && p.IsAvailable).ToList();
        var key = requestedKey?.Trim()
                  ?? (_config[$"MetadataProviders:{mediaType}"] ?? _config["MetadataProviders:DefaultProvider"])?.Trim();

        return (!string.IsNullOrEmpty(key) ? supporting.FirstOrDefault(p => p.ProviderKey.Equals(key, StringComparison.OrdinalIgnoreCase)) : null)
            ?? supporting.FirstOrDefault()                                            // any available real provider (Tmdb etc. before Seed — see _providers order in ctor)
            ?? _providers.FirstOrDefault(p => p.ProviderKey == "seed")                // ultimate fallback
            ?? _providers.First();
    }

    // ---- capability surface ----
    public string ProviderKey => "router";
    public bool RequiresApiKey => false;
    public bool IsAvailable => _providers.Count > 0;
    public bool Supports(MediaType mediaType) => _providers.Any(p => p.Supports(mediaType) && p.IsAvailable);

    // ---- routed calls ----
    public Task<List<MediaCardDto>> SearchAsync(string query, MediaType? mediaType = null, int page = 1,
        int pageSize = 20) => SearchAsync(new MediaSearchQuery
        {
            Query = query, MediaType = mediaType, Page = page, PageSize = pageSize
        });

    public async Task<List<MediaCardDto>> SearchAsync(MediaSearchQuery query)
    {
        var musicSettings = await _musicSettings.GetAsync();
        var musicEnabled = musicSettings.CatalogEnabled;
        if (query.MediaType == MediaType.Music || query.Kind is MediaKind.Album or MediaKind.Artist or MediaKind.Track)
            return musicEnabled ? await Pick(MediaType.Music, musicSettings.MetadataProvider).SearchAsync(query) : new();

        if (query.MediaType is MediaType requested)
            return await Pick(requested).SearchAsync(query);

        // Cross-media search keeps the existing TMDb mixed movie/TV result set and adds album matches.
        // Album is the default music kind here so one user search costs one catalog call, not three.
        var videoTask = Pick(MediaType.Movie).SearchAsync(query);
        if (!musicEnabled) return await videoTask;
        var musicTask = Pick(MediaType.Music, musicSettings.MetadataProvider).SearchAsync(new MediaSearchQuery
        {
            Query = query.Query,
            MediaType = MediaType.Music,
            Kind = MediaKind.Album,
            Page = query.Page,
            PageSize = Math.Min(8, query.PageSize)
        });
        await Task.WhenAll(videoTask, musicTask);
        return Interleave(videoTask.Result, musicTask.Result, query.PageSize);
    }

    public Task<MediaDetailDto?> GetDetailsAsync(int mediaId, MediaType mediaType) => Pick(mediaType).GetDetailsAsync(mediaId, mediaType);
    public Task<MediaDetailDto?> GetDetailsAsync(MediaRef mediaRef) => PickForIdentity(mediaRef).GetDetailsAsync(mediaRef);
    public Task<string?> GetImdbIdAsync(int mediaId, MediaType mediaType) => Pick(mediaType).GetImdbIdAsync(mediaId, mediaType);
    public Task<string?> GetImdbIdAsync(MediaRef mediaRef) => PickForIdentity(mediaRef).GetImdbIdAsync(mediaRef);
    public Task<List<MediaCardDto>> GetLibraryAsync(MediaType mediaType, int page = 1, int pageSize = 20)
        => PickDiscovery(mediaType).GetLibraryAsync(mediaType, page, pageSize);
    public Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10) => Pick(MediaType.Movie).GetRecentlyAddedAsync(count);

    public Task<List<MediaCardDto>> GetTrendingAsync(MediaType? mediaType = null, int page = 1, int pageSize = 20)
        => PickDiscovery(mediaType ?? MediaType.Movie).GetTrendingAsync(mediaType, page, pageSize);
    public Task<List<MediaCardDto>> GetPopularAsync(MediaType mediaType, int page = 1, int pageSize = 20)
        => PickDiscovery(mediaType).GetPopularAsync(mediaType, page, pageSize);
    public Task<List<MediaCardDto>> GetTopRatedAsync(MediaType mediaType, int page = 1, int pageSize = 20)
        => PickDiscovery(mediaType).GetTopRatedAsync(mediaType, page, pageSize);
    public Task<List<MediaCardDto>> GetByGenreAsync(MediaType mediaType, string genre, int page = 1, int pageSize = 20)
        => PickDiscovery(mediaType).GetByGenreAsync(mediaType, genre, page, pageSize);
    public Task<List<MediaCardDto>> GetSimilarAsync(int mediaId, MediaType mediaType, int count = 12) => Pick(mediaType).GetSimilarAsync(mediaId, mediaType, count);
    public Task<List<MediaCardDto>> GetSimilarAsync(MediaRef mediaRef, int count = 12) => PickForIdentity(mediaRef).GetSimilarAsync(mediaRef, count);
    public Task<List<EpisodeDto>> GetSeasonEpisodesAsync(int showId, int seasonNumber) => Pick(MediaType.TvShow).GetSeasonEpisodesAsync(showId, seasonNumber);

    private static List<MediaCardDto> Interleave(IReadOnlyList<MediaCardDto> primary,
        IReadOnlyList<MediaCardDto> secondary, int take)
    {
        var result = new List<MediaCardDto>(Math.Min(take, primary.Count + secondary.Count));
        for (var i = 0; result.Count < take && (i < primary.Count || i < secondary.Count); i++)
        {
            if (i < primary.Count) result.Add(primary[i]);
            if (result.Count < take && i < secondary.Count) result.Add(secondary[i]);
        }
        return result;
    }

    private IMediaMetadataProvider PickForIdentity(MediaRef mediaRef)
        => _providers.FirstOrDefault(p => p.IsAvailable && p.Supports(mediaRef.MediaType)
               && p.ProviderKey.Equals(mediaRef.Provider, StringComparison.OrdinalIgnoreCase))
           ?? Pick(mediaRef.MediaType);

    // ListenBrainz/MusicBrainz remains the standards-based discovery feed. Its cards carry MusicBrainz
    // identities, while the admin-selected provider controls user searches only.
    private IMediaMetadataProvider PickDiscovery(MediaType mediaType)
        => mediaType == MediaType.Music ? Pick(mediaType, "musicbrainz") : Pick(mediaType);
}
