using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Services.Implementations;

public interface IListenBrainzDiscoveryClient
{
    Task<List<MediaCardDto>> GetTrendingAlbumsAsync(int page, int pageSize,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional discovery companion to MusicBrainz. ListenBrainz supplies popularity from public,
/// site-wide listening statistics; the returned MBIDs still resolve through the normal MusicBrainz path.</summary>
public sealed class ListenBrainzDiscoveryClient(
    HttpClient http,
    IMemoryCache cache,
    ILogger<ListenBrainzDiscoveryClient> logger) : IListenBrainzDiscoveryClient
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    public async Task<List<MediaCardDto>> GetTrendingAlbumsAsync(int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        var safePage = Math.Max(1, page);
        var take = Math.Clamp(pageSize, 1, 100);
        var requested = Math.Min(100, safePage * take + 10); // duplicate MBIDs occasionally appear upstream
        var key = $"listenbrainz:sitewide:release-groups:{requested}";
        if (cache.TryGetValue<List<MediaCardDto>>(key, out var cached) && cached is not null)
            return Page(cached, safePage, take);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(
                    $"1/stats/sitewide/release-groups?range=this_week&count={requested}", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    var rows = Map(doc.RootElement);
                    cache.Set(key, rows, CacheTtl);
                    return Page(rows, safePage, take);
                }
                if (!IsTransient(response.StatusCode)) break;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                       && ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (attempt == 2) logger.LogWarning(ex, "ListenBrainz discovery exhausted retries");
            }
            if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), cancellationToken);
        }
        return new();
    }

    private static List<MediaCardDto> Map(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload)
            || !payload.TryGetProperty("release_groups", out var rows)
            || rows.ValueKind != JsonValueKind.Array) return new();

        return rows.EnumerateArray()
            .Select(row => new
            {
                Id = Text(row, "release_group_mbid"),
                Title = Text(row, "release_group_name"),
                Artist = Text(row, "artist_name"),
                Listens = Long(row, "listen_count")
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Title))
            .GroupBy(x => x.Id!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.Listens).First())
            .OrderByDescending(x => x.Listens)
            .Select(x =>
            {
                var mediaRef = MediaRef.FromExternal("musicbrainz", x.Id!, MediaType.Music, MediaKind.Album);
                return new MediaCardDto
                {
                    Title = x.Title!,
                    Subtitle = x.Artist,
                    MediaType = MediaType.Music,
                    ExternalId = mediaRef.Id,
                    ExternalSource = mediaRef.Provider,
                    MediaRef = mediaRef,
                    PosterUrl = $"https://coverartarchive.org/release-group/{Uri.EscapeDataString(mediaRef.Id)}/front-500"
                };
            }).ToList();
    }

    private static List<MediaCardDto> Page(List<MediaCardDto> rows, int page, int take)
        => rows.Skip((page - 1) * take).Take(take).ToList();

    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.TooManyRequests
        or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static string? Text(JsonElement row, string property)
        => row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static long Long(JsonElement row, string property)
        => row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var number) ? number : 0;
}
