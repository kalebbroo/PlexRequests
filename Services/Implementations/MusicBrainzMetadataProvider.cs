using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// Music metadata via the MusicBrainz API — KEYLESS (no API key required), which makes it the natural
/// default fallback for <see cref="MediaType.Music"/>. Currently a scaffold: it declares its
/// capabilities and returns empty results so the router can select it and the pipeline stays
/// music-aware. The real MusicBrainz calls are TODOs below.
///
/// Model note: MusicBrainz keys entities by MBID (a GUID string), so music flows through the pipeline
/// on the string <c>ExternalId</c> (not the TMDb int). See MediaRequestEntity.ExternalId.
/// </summary>
public class MusicBrainzMetadataProvider(HttpClient http, ILogger<MusicBrainzMetadataProvider> logger) : IMediaMetadataProvider
{
    private const string ApiBase = "https://musicbrainz.org/ws/2";
    // MusicBrainz requires a descriptive User-Agent; set on the typed client at registration.

    public string ProviderKey => "musicbrainz";
    public bool RequiresApiKey => false;                 // keyless -> good default fallback for music
    public bool IsAvailable => true;
    public bool Supports(MediaType mediaType) => mediaType == MediaType.Music;

    public Task<List<MediaCardDto>> SearchAsync(string query, MediaType? mediaType = null, int page = 1, int pageSize = 20)
    {
        // TODO(music): GET {ApiBase}/release-group?query={query}&fmt=json (albums) and/or /artist.
        // Map each result to a MediaCardDto with MediaType=Music, TmdbId=null, and the MBID carried in
        // a new external-id field (see TODO on MediaCardDto). Return card list.
        logger.LogDebug("MusicBrainz SearchAsync stub for '{Query}' — not yet implemented", query);
        return Task.FromResult(new List<MediaCardDto>());
    }

    public Task<MediaDetailDto?> GetDetailsAsync(int mediaId, MediaType mediaType)
    {
        // TODO(music): MusicBrainz is keyed by MBID (string), not an int. The router/caching layer key
        // by (MediaType, int) today; music details should be fetched via a string-id overload.
        // GET {ApiBase}/release-group/{mbid}?inc=artists+releases&fmt=json -> MediaDetailDto.
        return Task.FromResult<MediaDetailDto?>(null);
    }

    public Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10) => Task.FromResult(new List<MediaCardDto>());
    public Task<List<MediaCardDto>> GetLibraryAsync(MediaType mediaType, int page = 1, int pageSize = 20) => Task.FromResult(new List<MediaCardDto>());
    public Task<string?> GetImdbIdAsync(int mediaId, MediaType mediaType) => Task.FromResult<string?>(null);
    // Discovery (trending/popular/etc.) uses the interface default fallbacks — TODO: MusicBrainz has no
    // "trending"; back these with ListenBrainz or curated charts later, or leave delegating to library.
}
