using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Services.Implementations;

public sealed class MusicCatalogSearchService(
    IMediaMetadataProvider metadata,
    IMusicSettingsService settings,
    ILogger<MusicCatalogSearchService> logger) : IMusicCatalogSearchService
{
    private static readonly MediaKind[] SearchKinds = [MediaKind.Artist, MediaKind.Album, MediaKind.Track];

    public async Task<MusicCatalogSearchResultDto> SearchAsync(string query, MediaKind? kind = null,
        int pageSizePerKind = 16, CancellationToken cancellationToken = default)
    {
        var normalized = query?.Trim() ?? string.Empty;
        var result = new MusicCatalogSearchResultDto { Query = normalized };
        if (normalized.Length is < 2 or > 200 || !(await settings.GetAsync(cancellationToken)).CatalogEnabled)
            return result;

        var requestedKinds = kind is MediaKind.Artist or MediaKind.Album or MediaKind.Track
            ? [kind.Value]
            : SearchKinds;
        var take = Math.Clamp(pageSizePerKind, 1, 40);
        var searches = requestedKinds.ToDictionary(x => x,
            x => SearchKindAsync(normalized, x, take, cancellationToken));
        await Task.WhenAll(searches.Values);

        foreach (var (searchKind, task) in searches)
        {
            var items = (await task)
                .Where(x => x.MediaType == MediaType.Music && x.ResolveMediaRef().Kind == searchKind)
                .Where(x => x.ResolveMediaRef().IsValid)
                .DistinctBy(x => x.ResolveMediaRef().StableKey, StringComparer.Ordinal)
                // MusicBrainz can expose several recording identities with the same visible title and
                // artist. Those are useful catalog distinctions, but presenting three indistinguishable
                // cards is not useful to a requester. Keep the highest-ranked visible representative.
                .GroupBy(x => $"{x.Title.Trim()}\u001f{x.Subtitle?.Trim()}", StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderBy(x => Relevance(x, normalized))
                .ThenByDescending(x => x.Year ?? 0)
                .ToList();
            switch (searchKind)
            {
                case MediaKind.Artist: result.Artists = items; break;
                case MediaKind.Album: result.Albums = items; break;
                case MediaKind.Track: result.Tracks = items; break;
            }
        }
        return result;
    }

    private async Task<List<MediaCardDto>> SearchKindAsync(string query, MediaKind kind, int take,
        CancellationToken cancellationToken)
    {
        try
        {
            return await metadata.SearchAsync(new MediaSearchQuery
            {
                Query = query,
                MediaType = MediaType.Music,
                Kind = kind,
                PageSize = take
            }).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A partial search is much more useful than failing the entire page because one upstream
            // entity endpoint is temporarily unavailable.
            logger.LogWarning(ex, "Music {Kind} search failed for {Query}", kind, query);
            return new();
        }
    }

    private static int Relevance(MediaCardDto item, string query)
    {
        if (item.Title.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (item.Subtitle?.Equals(query, StringComparison.OrdinalIgnoreCase) == true) return 1;
        if (item.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (item.Subtitle?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) return 4;
        return 5;
    }
}
