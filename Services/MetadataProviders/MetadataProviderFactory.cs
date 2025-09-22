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

    public MetadataProviderFactory(IServiceProvider serviceProvider, IConfiguration configuration, ILogger<MetadataProviderFactory> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
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

    public IMediaMetadataProvider GetDefaultProvider()
    {
        var defaultProviderName = _configuration["MetadataProviders:DefaultProvider"] ?? "TMDb";

        var providerType = defaultProviderName.ToLower() switch
        {
            "tmdb" => MetadataProviderType.TMDb,
            "trakt" => MetadataProviderType.Trakt,
            "seed" => MetadataProviderType.Seed,
            _ => MetadataProviderType.Seed
        };

        _logger.LogInformation("Using default metadata provider: {Provider}", providerType);
        return GetProvider(providerType);
    }

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
