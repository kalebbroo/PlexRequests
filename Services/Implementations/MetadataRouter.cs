using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.MetadataProviders;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// The single metadata entry point ("one method for any media type"). Implements
/// <see cref="IMediaMetadataProvider"/> and routes each call to the provider the admin chose for that
/// media type (config <c>MetadataProviders:{Movie|TvShow|Music|Anime}</c>, or <c>:DefaultProvider</c>),
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
    private readonly List<IMediaMetadataProvider> _providers = new();

    public MetadataRouter(IServiceProvider sp, IConfiguration config, ILogger<MetadataRouter> logger)
    {
        _config = config;
        _logger = logger;
        // Order matters only for the "first keyless / first supporting" fallbacks.
        foreach (var type in new[]
                 {
                     typeof(TmdbMetadataProvider), typeof(TvdbMetadataProvider), typeof(TraktMetadataProvider),
                     typeof(MusicBrainzMetadataProvider), typeof(SeedMetadataProvider)
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
    private IMediaMetadataProvider Pick(MediaType mediaType)
    {
        var supporting = _providers.Where(p => p.Supports(mediaType) && p.IsAvailable).ToList();
        var key = (_config[$"MetadataProviders:{mediaType}"] ?? _config["MetadataProviders:DefaultProvider"])?.Trim();

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
    public Task<List<MediaCardDto>> SearchAsync(string query, MediaType? mediaType = null, int page = 1, int pageSize = 20)
        // TODO(modular): for a null (cross-type) search, aggregate results from every supporting provider
        // instead of routing to just the default. For now, route to the type's provider (or Movie default).
        => Pick(mediaType ?? MediaType.Movie).SearchAsync(query, mediaType, page, pageSize);

    public Task<MediaDetailDto?> GetDetailsAsync(int mediaId, MediaType mediaType) => Pick(mediaType).GetDetailsAsync(mediaId, mediaType);
    public Task<string?> GetImdbIdAsync(int mediaId, MediaType mediaType) => Pick(mediaType).GetImdbIdAsync(mediaId, mediaType);
    public Task<List<MediaCardDto>> GetLibraryAsync(MediaType mediaType, int page = 1, int pageSize = 20) => Pick(mediaType).GetLibraryAsync(mediaType, page, pageSize);
    public Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10) => Pick(MediaType.Movie).GetRecentlyAddedAsync(count);

    public Task<List<MediaCardDto>> GetTrendingAsync(MediaType? mediaType = null, int page = 1, int pageSize = 20) => Pick(mediaType ?? MediaType.Movie).GetTrendingAsync(mediaType, page, pageSize);
    public Task<List<MediaCardDto>> GetPopularAsync(MediaType mediaType, int page = 1, int pageSize = 20) => Pick(mediaType).GetPopularAsync(mediaType, page, pageSize);
    public Task<List<MediaCardDto>> GetTopRatedAsync(MediaType mediaType, int page = 1, int pageSize = 20) => Pick(mediaType).GetTopRatedAsync(mediaType, page, pageSize);
    public Task<List<MediaCardDto>> GetByGenreAsync(MediaType mediaType, string genre, int page = 1, int pageSize = 20) => Pick(mediaType).GetByGenreAsync(mediaType, genre, page, pageSize);
    public Task<List<MediaCardDto>> GetSimilarAsync(int mediaId, MediaType mediaType, int count = 12) => Pick(mediaType).GetSimilarAsync(mediaId, mediaType, count);
    public Task<List<EpisodeDto>> GetSeasonEpisodesAsync(int showId, int seasonNumber) => Pick(MediaType.TvShow).GetSeasonEpisodesAsync(showId, seasonNumber);
}
