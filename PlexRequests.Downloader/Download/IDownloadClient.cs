namespace PlexRequests.Downloader.Download;

/// <summary>Status of a torrent in the download client.</summary>
public record DownloadStatus(string State, double Progress, string Name, long TotalSizeBytes, string? SavePath, bool IsFinished);

/// <summary>Torrent client abstraction (implemented for Deluge; swappable).</summary>
public interface IDownloadClient
{
    /// <summary>Add a magnet, optionally labelled for library routing. Returns the torrent id/hash, or null on failure.</summary>
    Task<string?> AddMagnetAsync(string magnet, string? label, CancellationToken ct);
    Task<DownloadStatus?> GetStatusAsync(string torrentId, CancellationToken ct);
    Task<bool> RemoveAsync(string torrentId, bool removeData, CancellationToken ct);
}
