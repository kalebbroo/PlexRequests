namespace PlexRequestsHosted.Services.Abstractions;

/// <summary>One public upload read from an official channel. Only these fields are needed to decide episode identity.</summary>
public sealed record YouTubeCatalogVideo(string VideoId, string ChannelId, string Title, int DurationSeconds, bool IsLive);

public interface IYouTubeVideoCatalog
{
    /// <summary>Uploads of one channel. Null means the channel could not be read right now (no key, quota, network);
    /// an empty list means it was read and holds no videos. Callers must never treat null as "no match".</summary>
    Task<IReadOnlyList<YouTubeCatalogVideo>?> GetChannelUploadsAsync(string channelId, CancellationToken cancellationToken = default);
}
