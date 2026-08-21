using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

public interface IPlexMusicService
{
    Task<MusicSearchResultDto> SearchAsync(string query, int limit = 30);
    Task<List<MusicAlbumDto>> GetArtistAlbumsAsync(string artistRatingKey);
    /// <summary>Is an album (by artist + title) already on Plex? Used for request dedup / availability.</summary>
    Task<bool> IsAlbumOnPlexAsync(string artist, string album);
    /// <summary>Verify a requested music identity against Plex after a scan. Provider ids are preferred;
    /// normalized title/artist matching is the fallback for Plex agents that do not expose MusicBrainz ids.</summary>
    Task<PlexVerificationResult> VerifyAsync(PlexVerificationRequest request, CancellationToken ct = default);
}

/// <summary>
/// Plex music-library access, modeled on PlexBot's artist→album→track approach but using this app's
/// config/HTTP. Music lives in a Plex section of type "artist"; items are a 3-level ratingKey tree.
/// This is the foundation for music requests (request an album/artist); availability is matched by
/// name since music has no TMDB id.
/// </summary>
public class PlexMusicService(HttpClient http, IOptions<PlexConfiguration> options, ILogger<PlexMusicService> logger) : IPlexMusicService
{
    private const int TrackDurationToleranceMs = 8_000;
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

    public async Task<PlexVerificationResult> VerifyAsync(PlexVerificationRequest request, CancellationToken ct = default)
    {
        if (!Configured) return new(false, Detail: "Plex is not configured");
        var query = request.Kind switch
        {
            Shared.Enums.MediaKind.Artist => request.Artist,
            Shared.Enums.MediaKind.Track => request.Track,
            _ => request.Album
        };
        if (string.IsNullOrWhiteSpace(query)) return new(false, Detail: "Music verification identity is incomplete");

        try
        {
            using var doc = await GetJsonAsync($"{Base}/hubs/search?query={Uri.EscapeDataString(query)}&limit=50&X-Plex-Token={Tok}", ct);
            if (doc is null) return new(false, Detail: "Plex search did not return a response");
            var mc = doc.RootElement.TryGetProperty("MediaContainer", out var m) ? m : doc.RootElement;
            if (!mc.TryGetProperty("Hub", out var hubs) || hubs.ValueKind != JsonValueKind.Array)
                return new(false, Detail: "Plex returned no matching music hubs");

            foreach (var hub in hubs.EnumerateArray())
            {
                if (!hub.TryGetProperty("Metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in metadata.EnumerateArray())
                {
                    if (!MatchesKind(item, request.Kind)) continue;
                    var ratingKey = Str(item, "ratingKey");
                    var byId = HasExternalId(item, request.ExternalSource, request.ExternalId);
                    if (!byId)
                    {
                        var matched = request.Kind == Shared.Enums.MediaKind.Track
                            ? await MatchesTrackAsync(item, ratingKey, request, ct)
                            : MatchesNames(item, request);
                        if (!matched) continue;
                    }
                    if (request.Kind == Shared.Enums.MediaKind.Artist)
                        return await VerifyArtistCatalogAsync(ratingKey, request.ExpectedAlbums, ct);
                    if (request.Kind == Shared.Enums.MediaKind.Album)
                        return await VerifyAlbumTracksAsync(ratingKey, request.ExpectedTracks, ct);
                    return new(true, ratingKey, byId
                        ? "Matched Plex provider id"
                        : "Matched normalized music identity");
                }
            }
            return new(false, Detail: "Plex has not indexed the requested music yet");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Plex music verification failed for {Kind} {Query}", request.Kind, query);
            return new(false, Detail: "Plex verification is temporarily unavailable");
        }
    }

    private async Task<PlexVerificationResult> VerifyArtistCatalogAsync(string ratingKey,
        IReadOnlyList<string>? expectedAlbums, CancellationToken ct)
    {
        if (expectedAlbums is not { Count: > 0 })
            return new(false, ratingKey, "Artist metadata did not contain an album completion contract");
        ct.ThrowIfCancellationRequested();
        var albums = await GetArtistAlbumsAsync(ratingKey);
        var have = albums.Select(a => Normalize(a.Title)).Where(x => x.Length > 0).ToHashSet();
        var missing = expectedAlbums.Where(x => !have.Contains(Normalize(x))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return missing.Count == 0
            ? new(true, ratingKey, $"Plex contains all {expectedAlbums.Count} expected artist releases")
            : new(false, ratingKey, $"Plex artist catalog is missing {missing.Count} expected release(s)");
    }

    private async Task<PlexVerificationResult> VerifyAlbumTracksAsync(string ratingKey,
        IReadOnlyList<string>? expectedTracks, CancellationToken ct)
    {
        if (expectedTracks is not { Count: > 0 })
            return new(false, ratingKey, "Album metadata did not contain a track completion contract");
        ct.ThrowIfCancellationRequested();
        using var doc = await GetJsonAsync(
            $"{Base}/library/metadata/{Uri.EscapeDataString(ratingKey)}/children?X-Plex-Token={Tok}", ct);
        if (doc is null) return new(false, ratingKey, "Plex did not return the album track list");
        var container = doc.RootElement.TryGetProperty("MediaContainer", out var mediaContainer)
            ? mediaContainer : doc.RootElement;
        var actualTracks = container.TryGetProperty("Metadata", out var metadata)
                           && metadata.ValueKind == JsonValueKind.Array
            ? metadata.EnumerateArray().Where(x => Str(x, "type") == "track")
                .Select(x => Str(x, "title")).ToList()
            : new List<string>();

        var have = actualTracks.Select(Normalize).Where(x => x.Length > 0)
            .GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
        var missing = new List<string>();
        foreach (var track in expectedTracks)
        {
            var key = Normalize(track);
            if (key.Length > 0 && have.TryGetValue(key, out var count) && count > 0)
                have[key] = count - 1;
            else
                missing.Add(track);
        }
        return missing.Count == 0
            ? new(true, ratingKey, $"Plex contains all {expectedTracks.Count} expected album tracks")
            : new(false, ratingKey, $"Plex album is missing {missing.Count} of {expectedTracks.Count} expected track(s)");
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    private static bool MatchesKind(JsonElement item, Shared.Enums.MediaKind kind)
    {
        var type = Str(item, "type");
        return kind switch
        {
            Shared.Enums.MediaKind.Artist => type == "artist",
            Shared.Enums.MediaKind.Track => type == "track",
            _ => type == "album"
        };
    }

    private static bool MatchesNames(JsonElement item, PlexVerificationRequest request)
    {
        var title = Str(item, "title");
        var artist = request.Kind == Shared.Enums.MediaKind.Track
            ? Str(item, "grandparentTitle")
            : request.Kind == Shared.Enums.MediaKind.Album ? Str(item, "parentTitle") : title;
        return request.Kind switch
        {
            Shared.Enums.MediaKind.Artist => Same(title, request.Artist),
            Shared.Enums.MediaKind.Track => MatchesTrack(item, request),
            _ => Same(title, request.Album) && Same(artist, request.Artist)
        };
    }

    private async Task<bool> MatchesTrackAsync(JsonElement item, string ratingKey,
        PlexVerificationRequest request, CancellationToken ct)
    {
        var title = Str(item, "title");
        var artist = Str(item, "grandparentTitle");
        var album = Str(item, "parentTitle");
        var duration = Int(item, "duration");
        // Explicitly different search metadata is authoritative enough to reject without another Plex call.
        // Fetch the full item only when the compact hub omitted a field needed by the contract.
        if ((title.Length > 0 && !Same(title, request.Track))
            || (artist.Length > 0 && !Same(artist, request.Artist))
            || (album.Length > 0 && !string.IsNullOrWhiteSpace(request.Album) && !Same(album, request.Album))
            || (duration is > 0 && request.ExpectedDurationMs is > 0
                && Math.Abs(duration.Value - request.ExpectedDurationMs.Value) > TrackDurationToleranceMs))
            return false;
        var incomplete = title.Length == 0 || artist.Length == 0
            || (!string.IsNullOrWhiteSpace(request.Album) && album.Length == 0)
            || (request.ExpectedDurationMs is > 0 && duration is not > 0);
        if (!incomplete) return true;
        if (string.IsNullOrWhiteSpace(ratingKey)) return false;
        using var doc = await GetJsonAsync(
            $"{Base}/library/metadata/{Uri.EscapeDataString(ratingKey)}?X-Plex-Token={Tok}", ct);
        if (doc is null) return false;
        var container = doc.RootElement.TryGetProperty("MediaContainer", out var mediaContainer)
            ? mediaContainer : doc.RootElement;
        if (!container.TryGetProperty("Metadata", out var metadata)
            || metadata.ValueKind != JsonValueKind.Array) return false;
        return metadata.EnumerateArray().Any(x => Str(x, "type") == "track" && MatchesTrack(x, request));
    }

    private static bool MatchesTrack(JsonElement item, PlexVerificationRequest request)
    {
        if (!Same(Str(item, "title"), request.Track)
            || !Same(Str(item, "grandparentTitle"), request.Artist)) return false;
        if (!string.IsNullOrWhiteSpace(request.Album)
            && !Same(Str(item, "parentTitle"), request.Album)) return false;
        var duration = Int(item, "duration");
        return request.ExpectedDurationMs is not > 0 || duration is not > 0
            || Math.Abs(duration.Value - request.ExpectedDurationMs.Value) <= TrackDurationToleranceMs;
    }

    private static bool HasExternalId(JsonElement item, string? source, string? externalId)
    {
        if (!string.Equals(source, "musicbrainz", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(externalId)) return false;
        var wanted = Normalize(externalId);
        if (item.TryGetProperty("guid", out var direct) && direct.ValueKind == JsonValueKind.String
            && Normalize(direct.GetString()).Contains(wanted, StringComparison.Ordinal)) return true;
        if (!item.TryGetProperty("Guid", out var guids) || guids.ValueKind != JsonValueKind.Array) return false;
        return guids.EnumerateArray().Any(g => g.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
            && Normalize(id.GetString()).Contains(wanted, StringComparison.Ordinal));
    }

    private static bool Same(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        return a.Length > 0 && b.Length > 0 && a == b;
    }

    private static string Normalize(string? value) => new((value ?? string.Empty)
        .Normalize(System.Text.NormalizationForm.FormD)
        .Where(c => char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant).ToArray());

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
