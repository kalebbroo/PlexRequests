using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Services.Implementations;

namespace PlexRequestsHosted.Services.MetadataProviders;

public enum MetadataProviderType
{
    TMDb,
    Trakt,
    Seed
}

public interface IMetadataProviderFactory
{
    IMediaMetadataProvider GetProvider(MetadataProviderType providerType);
    IMediaMetadataProvider GetDefaultProvider();
    IEnumerable<MetadataProviderType> GetAvailableProviders();
}

public class MetadataProviderFactory : IMetadataProviderFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MetadataProviderFactory> _logger;
    private readonly MetadataProviderType _defaultProviderType;
    private readonly IMediaMetadataProvider _defaultProvider;

    public MetadataProviderFactory(IServiceProvider serviceProvider, IConfiguration configuration, ILogger<MetadataProviderFactory> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;

        // Resolve and cache default provider once at startup; log only once
        var defaultProviderName = _configuration["MetadataProviders:DefaultProvider"] ?? "TMDb";
        _defaultProviderType = defaultProviderName.ToLower() switch
        {
            "tmdb" => MetadataProviderType.TMDb,
            "trakt" => MetadataProviderType.Trakt,
            "seed" => MetadataProviderType.Seed,
            _ => MetadataProviderType.Seed
        };
        _logger.LogInformation("Using default metadata provider: {Provider}", _defaultProviderType);
        _defaultProvider = GetProvider(_defaultProviderType);
    }

    public IMediaMetadataProvider GetProvider(MetadataProviderType providerType)
    {
        return providerType switch
        {
            MetadataProviderType.TMDb => _serviceProvider.GetRequiredService<TmdbMetadataProvider>(),
            MetadataProviderType.Trakt => _serviceProvider.GetRequiredService<TraktMetadataProvider>(),
            MetadataProviderType.Seed => _serviceProvider.GetRequiredService<SeedMetadataProvider>(),
            _ => throw new ArgumentException($"Unsupported provider type: {providerType}", nameof(providerType))
        };
    }

    public IMediaMetadataProvider GetDefaultProvider() => _defaultProvider;

    public IEnumerable<MetadataProviderType> GetAvailableProviders()
    {
        var configuredProviders = _configuration.GetSection("MetadataProviders:AvailableProviders")
            .Get<string[]>() ?? new[] { "TMDb", "Trakt", "Seed" };

        return configuredProviders
            .Select(p => p.ToLower() switch
            {
                "tmdb" => MetadataProviderType.TMDb,
                "trakt" => MetadataProviderType.Trakt,
                "seed" => MetadataProviderType.Seed,
                _ => (MetadataProviderType?)null
            })
            .Where(p => p.HasValue)
            .Select(p => p!.Value);
    }
}
