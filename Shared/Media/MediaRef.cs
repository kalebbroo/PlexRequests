using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared.Media;

/// <summary>
/// Provider-neutral identity carried across UI, API, request and worker boundaries. Provider and id are
/// strings deliberately: TMDb happens to use integers, while MusicBrainz uses UUIDs and Plex rating keys are
/// opaque strings. <see cref="StableKey"/> is suitable for dictionaries and idempotency keys, not display.
/// </summary>
public sealed record MediaRef
{
    [Required, MaxLength(32)]
    public required string Provider { get; init; }

    [Required, MaxLength(128)]
    public required string Id { get; init; }

    public required MediaType MediaType { get; init; }
    public required MediaKind Kind { get; init; }

    public string StableKey
    {
        get
        {
            var provider = NormalizeProvider(Provider);
            return $"{MediaType}:{Kind}:{provider}:{NormalizeId(provider, Id)}";
        }
    }

    public bool IsValid => !string.IsNullOrWhiteSpace(Provider)
                           && Provider.Length <= 32
                           && !string.IsNullOrWhiteSpace(Id)
                           && Id.Length <= 128
                           && Kind.BelongsTo(MediaType)
                           && (!Provider.Equals("tmdb", StringComparison.OrdinalIgnoreCase)
                               || int.TryParse(Id, out var tmdbId) && tmdbId > 0);

    public static MediaRef FromTmdb(int id, MediaType mediaType) => new()
    {
        Provider = "tmdb",
        Id = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        MediaType = mediaType,
        Kind = mediaType.DefaultKind()
    };

    public static MediaRef FromExternal(string provider, string id, MediaType mediaType, MediaKind kind) => new()
    {
        Provider = NormalizeProvider(provider),
        Id = NormalizeId(NormalizeProvider(provider), id),
        MediaType = mediaType,
        Kind = kind
    };

    public bool TryGetTmdbId(out int id)
    {
        id = 0;
        return Provider.Equals("tmdb", StringComparison.OrdinalIgnoreCase)
               && int.TryParse(Id, System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out id);
    }

    public static string NormalizeProvider(string value) => value.Trim().ToLowerInvariant();

    // MusicBrainz UUIDs are case-insensitive. Unknown/provider-defined ids are not: YouTube ids, for
    // example, are case-sensitive, so a global ToLowerInvariant would silently merge distinct media.
    public static string NormalizeId(string provider, string value) => NormalizeProvider(provider) switch
    {
        "musicbrainz" or "tmdb" => value.Trim().ToLowerInvariant(),
        _ => value.Trim()
    };
}

public static class MediaIdentityExtensions
{
    public static MediaKind DefaultKind(this MediaType mediaType) => mediaType switch
    {
        MediaType.Movie => MediaKind.Movie,
        MediaType.TvShow or MediaType.Anime => MediaKind.Series,
        MediaType.Music => MediaKind.Album,
        _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, null)
    };

    public static bool BelongsTo(this MediaKind kind, MediaType mediaType) => mediaType switch
    {
        MediaType.Movie => kind == MediaKind.Movie,
        MediaType.TvShow or MediaType.Anime => kind == MediaKind.Series,
        MediaType.Music => kind is MediaKind.Album or MediaKind.Artist or MediaKind.Track,
        _ => false
    };

    public static RequestScopeKind DefaultScope(this MediaKind kind) => kind switch
    {
        MediaKind.Movie => RequestScopeKind.Title,
        MediaKind.Series => RequestScopeKind.Series,
        MediaKind.Album => RequestScopeKind.Album,
        MediaKind.Artist => RequestScopeKind.ArtistCatalog,
        MediaKind.Track => RequestScopeKind.Track,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>Resolve durable request intent back to its media shape. Keeping this inverse mapping beside
    /// <see cref="DefaultScope"/> prevents queueing, ranking, bridge events, and reconciliation from each
    /// inventing subtly different wrong-scope fallbacks.</summary>
    public static MediaKind ToMediaKind(this RequestScopeKind scope, MediaType mediaType) => scope switch
    {
        RequestScopeKind.ArtistCatalog when mediaType == MediaType.Music => MediaKind.Artist,
        RequestScopeKind.Track when mediaType == MediaType.Music => MediaKind.Track,
        RequestScopeKind.Album when mediaType == MediaType.Music => MediaKind.Album,
        RequestScopeKind.Series or RequestScopeKind.Seasons or RequestScopeKind.Episodes
            when mediaType is MediaType.TvShow or MediaType.Anime => MediaKind.Series,
        _ => mediaType.DefaultKind()
    };
}

/// <summary>
/// Request intent carried by API clients. Existing season/episode columns remain the typed persistence for
/// video selections; the scope kind prevents other media modules from inheriting TV-specific assumptions.
/// </summary>
public sealed class MediaRequestScope
{
    public RequestScopeKind Kind { get; set; }
    public List<int> Seasons { get; set; } = new();
    public List<EpisodeSelection> Episodes { get; set; } = new();
}

public sealed record EpisodeSelection(int Season, int Episode);

/// <summary>Provider-neutral search intent. Kind is optional so existing movie/TV callers stay simple,
/// while modules such as music can distinguish albums, artists and tracks without string conventions.</summary>
public sealed class MediaSearchQuery
{
    public required string Query { get; set; }
    public MediaType? MediaType { get; set; }
    public MediaKind? Kind { get; set; }
    /// <summary>Optional provider preference for a provider-specific experience. The metadata router
    /// validates it against enabled providers and otherwise falls back to the configured provider.</summary>
    public string? Provider { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
