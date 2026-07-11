using System.Text.Json;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Indexers;

/// <summary>A single indexer source (one site/API). Providers are composed by <see cref="IIndexerClient"/>.</summary>
public interface IIndexerProvider
{
    string Name { get; }
    bool Supports(MediaType mediaType);
    Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(FulfillmentJobDto job, CancellationToken ct);
}

/// <summary>Aggregates all applicable providers for a job and merges their results.</summary>
public interface IIndexerClient
{
    Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(FulfillmentJobDto job, CancellationToken ct);
}

/// <summary>Shared JSON options: snake_case (EZTV/YTS) + numbers that may arrive as strings (EZTV).</summary>
public static class IndexerJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };
}
