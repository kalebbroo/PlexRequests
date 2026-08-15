namespace PlexRequestsHosted.Services.Abstractions;

/// <summary>
/// Fetches Plex-owned artwork without exposing the server token or private server URL to the browser.
/// Only Plex metadata paths and Plex-hosted account avatars are accepted.
/// </summary>
public interface IPlexArtworkService
{
    Task<PlexArtwork?> GetAsync(string source, int width, int height, CancellationToken cancellationToken = default);
}

public sealed record PlexArtwork(byte[] Bytes, string ContentType);
