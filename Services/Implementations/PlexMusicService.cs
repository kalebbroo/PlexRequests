using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

public interface IPlexMusicService
{
    Task<MusicSearchResultDto> SearchAsync(string query, int limit = 30);
    Task<List<MusicAlbumDto>> GetArtistAlbumsAsync(string artistRatingKey);
    /// <summary>Is an album (by artist + title) already on Plex? Used for request dedup / availability.</summary>
    Task<bool> IsAlbumOnPlexAsync(string artist, string album);
}

/// <summary>
/// Plex music-library access, modeled on PlexBot's artist→album→track approach but using this app's
/// config/HTTP. Music lives in a Plex section of type "artist"; items are a 3-level ratingKey tree.
/// This is the foundation for music requests (request an album/artist); availability is matched by
/// name since music has no TMDB id.
/// </summary>
public class PlexMusicService(HttpClient http, IOptions<PlexConfiguration> options, IMemoryCache cache, ILogger<PlexMusicService> logger) : IPlexMusicService
{
    private readonly PlexConfiguration _cfg = options.Value;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private bool Configured => !string.IsNullOrWhiteSpace(_cfg.PrimaryServerUrl) && !string.IsNullOrWhiteSpace(_cfg.ServerToken);
    private string Base => (_cfg.PrimaryServerUrl ?? string.Empty).TrimEnd('/');
    private string Tok => Uri.EscapeDataString(_cfg.ServerToken ?? string.Empty);

    public async Task<MusicSearchResultDto> SearchAsync(string query, int limit = 30)
    {
        var result = new MusicSearchResultDto();
        if (!Configured || string.IsNullOrWhiteSpace(query)) return result;
        try
        {
            var doc = await GetJsonAsync($"{Base}/hubs/search?query={Uri.EscapeDataString(query)}&limit={limit}&X-Plex-Token={Tok}");
            if (doc is null) return result;
            var mc = doc.RootElement.TryGetProperty("MediaContainer", out var m) ? m : doc.RootElement;
            if (!mc.TryGetProperty("Hub", out var hubs) || hubs.ValueKind != JsonValueKind.Array) return result;

            foreach (var hub in hubs.EnumerateArray())
            {
                var type = Str(hub, "type");
                if (!hub.TryGetProperty("Metadata", out var md) || md.ValueKind != JsonValueKind.Array) continue;
                foreach (var it in md.EnumerateArray())
                {
                    switch (type)
                    {
                        case "artist":
                            result.Artists.Add(new MusicArtistDto { RatingKey = Str(it, "ratingKey"), Name = Str(it, "title"), Genre = FirstTag(it, "Genre"), ArtworkUrl = Art(it), Key = Str(it, "key") });
                            break;
                        case "album":
                            result.Albums.Add(new MusicAlbumDto { RatingKey = Str(it, "ratingKey"), Title = Str(it, "title"), Artist = Str(it, "parentTitle"), Year = Int(it, "year"), ArtworkUrl = Art(it), Key = Str(it, "key"), IsAvailable = true });
                            break;
                        case "track":
                            result.Tracks.Add(new MusicTrackDto { RatingKey = Str(it, "ratingKey"), Title = Str(it, "title"), Artist = Str(it, "grandparentTitle"), Album = Str(it, "parentTitle"), DurationMs = Int(it, "duration") });
                            break;
                    }
                }
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Plex music search failed for {Query}", query); }
        return result;
    }

    public async Task<List<MusicAlbumDto>> GetArtistAlbumsAsync(string artistRatingKey)
    {
        var albums = new List<MusicAlbumDto>();
        if (!Configured || string.IsNullOrWhiteSpace(artistRatingKey)) return albums;
        try
        {
            var doc = await GetJsonAsync($"{Base}/library/metadata/{artistRatingKey}/children?X-Plex-Token={Tok}");
            var mc = doc?.RootElement.TryGetProperty("MediaContainer", out var m) == true ? m : doc?.RootElement;
            if (mc is { ValueKind: JsonValueKind.Object } mcv && mcv.TryGetProperty("Metadata", out var md) && md.ValueKind == JsonValueKind.Array)
                foreach (var it in md.EnumerateArray())
                    if (Str(it, "type") == "album")
                        albums.Add(new MusicAlbumDto { RatingKey = Str(it, "ratingKey"), Title = Str(it, "title"), Artist = Str(it, "parentTitle"), Year = Int(it, "year"), ArtworkUrl = Art(it), Key = Str(it, "key"), IsAvailable = true });
        }
        catch (Exception ex) { logger.LogWarning(ex, "Plex artist albums failed for {Key}", artistRatingKey); }
        return albums;
    }

    public async Task<bool> IsAlbumOnPlexAsync(string artist, string album)
    {
        if (string.IsNullOrWhiteSpace(album)) return false;
        var res = await SearchAsync(album, 30);
        return res.Albums.Any(a =>
            string.Equals(a.Title, album, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(artist) || string.Equals(a.Artist, artist, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<JsonDocument?> GetJsonAsync(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var res = await http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync());
    }

    private string? Art(JsonElement el)
    {
        var thumb = Str(el, "thumb");
        return string.IsNullOrEmpty(thumb) ? null : $"{Base}{thumb}?X-Plex-Token={Tok}";
    }

    private static string Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var p) ? (p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : p.ValueKind == JsonValueKind.Number ? p.GetInt64().ToString() : "") : "";

    private static int? Int(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : null;

    private static string? FirstTag(JsonElement el, string arrayProp)
        => el.TryGetProperty(arrayProp, out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0
            && a[0].TryGetProperty("tag", out var t) ? t.GetString() : null;
}
