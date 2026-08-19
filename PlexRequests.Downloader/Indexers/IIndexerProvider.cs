using System.Text.Json;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// One way of searching an indexer — the code, not the configuration. A single implementation serves any
/// number of configured indexers: "Torznab" backs every Jackett/Prowlarr endpoint the admin adds.
///
/// This used to be one class per indexer with its name, media-type support and enable flag compiled in,
/// which is why several Torznab endpoints could only ever share one identity. All of that now arrives per
/// call in <see cref="IndexerConfigDto"/>, so the same code serves many differently-configured rows.
/// </summary>
public interface IIndexerImplementation
{
    /// <summary>Matches <see cref="IndexerConfigDto.Implementation"/>. Unique across implementations.</summary>
    string Key { get; }

    /// <summary>Search one configured indexer. Throwing is fine — the client isolates and reports failures.</summary>
    Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(IndexerConfigDto indexer, FulfillmentJobDto job, CancellationToken ct);

    /// <summary>
    /// The indexer's newest uploads, for the home page's "Recommended" row. Distinct from searching: there's
    /// no query, results come back newest-first, and no magnet is needed — a poster row only needs a title.
    ///
    /// Most implementations are search-only APIs with no browsable listing, so the default is "nothing".
    /// </summary>
    Task<IReadOnlyList<ReleaseCandidate>> BrowseLatestAsync(IndexerConfigDto indexer, MediaType mediaType, int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ReleaseCandidate>>(Array.Empty<ReleaseCandidate>());

    /// <summary>
    /// Probe the endpoint and report what it can do. Torznab overrides this with a real <c>t=caps</c> call;
    /// the built-in scrapers have nothing to ask, so the default runs a throwaway search and reports
    /// whether the site answered — which is the part an admin actually needs (a scraper that has been
    /// Cloudflare-blocked looks identical to one that simply found nothing).
    /// </summary>
    async Task<IndexerCapabilitiesDto> TestAsync(IndexerConfigDto indexer, CancellationToken ct)
    {
        var probeType = indexer.Supports(MediaType.Movie) ? MediaType.Movie
            : indexer.Supports(MediaType.TvShow) ? MediaType.TvShow
            : indexer.Supports(MediaType.Music) ? MediaType.Music
            : MediaType.Movie;
        var probe = new FulfillmentJobDto
        {
            Title = "the",
            MediaType = probeType,
            RequestScope = probeType == MediaType.Music ? RequestScopeKind.Album : RequestScopeKind.Title,
            Music = probeType == MediaType.Music
                ? new MusicAcquisitionContextDto { Kind = MediaKind.Album, Album = "the" }
                : null,
            Quality = Quality.Any
        };
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var results = await SearchAsync(indexer, probe, ct);
            started.Stop();
            return new IndexerCapabilitiesDto
            {
                Reachable = true,
                ResponseMs = (int)started.ElapsedMilliseconds,
                SupportsMovieSearch = indexer.SupportsMovie,
                SupportsTvSearch = indexer.SupportsTv,
                SupportsMusicSearch = indexer.Supports(MediaType.Music),
                Message = $"Responded in {started.ElapsedMilliseconds} ms with {results.Count} result(s) for a sample query."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            started.Stop();
            return new IndexerCapabilitiesDto { Reachable = false, Message = ex.Message };
        }
    }
}

/// <summary>Aggregates all applicable indexers for a job and merges their results, with a per-indexer
/// outcome breakdown (see <see cref="IndexerSearchResult"/>).</summary>
public interface IIndexerClient
{
    Task<IndexerSearchResult> SearchAsync(
        FulfillmentJobDto job,
        CancellationToken ct,
        IndexerSearchPurpose purpose = IndexerSearchPurpose.Automatic);

    /// <summary>Probe one configured indexer by id. Never throws — a failed probe is a result, not an error.</summary>
    Task<IndexerCapabilitiesDto> TestAsync(int indexerId, CancellationToken ct);
}

public enum IndexerSearchPurpose
{
    Automatic,
    Interactive,
    Monitoring
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
