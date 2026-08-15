using Microsoft.Extensions.Options;
using PlexRequestsHosted.Services.Abstractions;

namespace PlexRequestsHosted.Services.Implementations;

public sealed class PlexArtworkService(
    HttpClient http,
    IOptions<PlexConfiguration> options,
    ILogger<PlexArtworkService> logger) : IPlexArtworkService
{
    private const int MaxArtworkBytes = 12 * 1024 * 1024;
    private readonly PlexConfiguration _config = options.Value;

    public async Task<PlexArtwork?> GetAsync(
        string source,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        width = Math.Clamp(width, 32, 1600);
        height = Math.Clamp(height, 32, 1600);

        var isPlexHostedAvatar = Uri.TryCreate(source, UriKind.Absolute, out var absolute) &&
            absolute.Scheme == Uri.UriSchemeHttps &&
            (absolute.Host.Equals("plex.tv", StringComparison.OrdinalIgnoreCase) ||
             absolute.Host.EndsWith(".plex.tv", StringComparison.OrdinalIgnoreCase));

        string url;
        var includeToken = false;
        if (isPlexHostedAvatar)
        {
            url = absolute!.AbsoluteUri;
            includeToken = true;
        }
        else
        {
            if (!IsAllowedMetadataPath(source) ||
                !Uri.TryCreate(NormalizeBaseUrl(_config.PrimaryServerUrl), UriKind.Absolute, out var server)) return null;
            url = $"{server.GetLeftPart(UriPartial.Authority)}/photo/:/transcode" +
                  $"?width={width}&height={height}&minSize=1&upscale=1&url={Uri.EscapeDataString(source)}";
            includeToken = true;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (includeToken && !string.IsNullOrWhiteSpace(_config.ServerToken))
                request.Headers.Add("X-Plex-Token", _config.ServerToken);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength is > MaxArtworkBytes) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0 || bytes.Length > MaxArtworkBytes) return null;
            return new PlexArtwork(bytes, response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Plex artwork fetch failed");
            return null;
        }
    }

    private static bool IsAllowedMetadataPath(string source) =>
        source.StartsWith("/library/metadata/", StringComparison.Ordinal) &&
        !source.Contains("..", StringComparison.Ordinal) &&
        !source.Contains('\\') &&
        !source.Contains('\r') &&
        !source.Contains('\n');

    private static string? NormalizeBaseUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var value = input.Trim().TrimEnd('/');
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) value = "http://" + value;
        return value;
    }
}
